﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Google;
using Google.Api.Gax.Grpc;
using Google.Cloud.PubSub.V1;
using Google.Protobuf;
using Grpc.Core;
using Rebus.Bus;
using Rebus.Exceptions;
using Rebus.Extensions;
using Rebus.Logging;
using Rebus.Messages;
using Rebus.Transport;

namespace Rebus.GoogleCloudPubSub
{
    public class GoogleCloudPubSubTransport : AbstractRebusTransport, IInitializable, IDisposable
    {
        private readonly ConcurrentDictionary<string, Lazy<Task<PublisherClient>>> _clients = new ConcurrentDictionary<string, Lazy<Task<PublisherClient>>>();
        private readonly string _projectId;
        private readonly string _inputQueueName;

        private TopicName _inputTopic;
        private SubscriberServiceApiClient _subscriberClient;
        private SubscriptionName _subscriptionName;
        protected readonly ILog Log;
        public GoogleCloudPubSubTransport(string projectId, string inputQueueName, IRebusLoggerFactory rebusLoggerFactory) : base(inputQueueName)
        {
            _projectId = projectId ?? throw new ArgumentNullException(nameof(projectId));
            _inputQueueName = inputQueueName;
            if (rebusLoggerFactory == null) throw new ArgumentNullException(nameof(rebusLoggerFactory));
            Log = rebusLoggerFactory.GetLogger<GoogleCloudPubSubTransport>();
        }

        public override void CreateQueue(string address)
        {
            var service = PublisherServiceApiClient.Create();
            var topic = new TopicName(_projectId, address);
            try
            {
                service.GetTopic(topic);
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)
            {
                service.CreateTopic(topic);
                Log.Info("Created topic {topic} ", _inputTopic);
            }
        }

        public void Initialize()
        {
            if (!string.IsNullOrEmpty(_inputQueueName))
            {
                _inputTopic = new TopicName(_projectId, _inputQueueName);
                CreateQueue(_inputQueueName);
                AsyncHelpers.RunSync(CreateSubscriptionAsync);
            }
        }

        public async Task PurgeQueueAsync()
        {
            var topic = new TopicName(_projectId, _inputQueueName);
            try
            {

                var service = await PublisherServiceApiClient.CreateAsync();
                await service.DeleteTopicAsync(topic);
                Log.Info("Purged topic {topic} by deleting it", topic.ToString());

            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)
            {
                Log.Warn("Tried purging topic {topic} by deleting it, but it could not be found", topic.ToString());
            }
            _subscriptionName = SubscriptionName.FromProjectSubscription(_projectId, _inputQueueName);
            try
            {

                _subscriberClient = await SubscriberServiceApiClient.CreateAsync();
                await _subscriberClient.DeleteSubscriptionAsync(_subscriptionName);
                Log.Info("Purged subscription {subscriptionname} by deleting it", _subscriptionName.ToString());
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)
            {
                Log.Info("Tried purging subscription {subscriptionname} by deleting it, but it could not be found", _subscriptionName.ToString());
            }
        }

        private async Task CreateSubscriptionAsync()
        {
            _subscriptionName = SubscriptionName.FromProjectSubscription(_projectId, _inputQueueName);
            _subscriberClient = await SubscriberServiceApiClient.CreateAsync();
            try
            {
                await _subscriberClient.GetSubscriptionAsync(_subscriptionName);
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)
            {
                int retries = 0;
                var maxRetries = 10;
                while (retries < maxRetries)
                {
                    try
                    {
                        await _subscriberClient.CreateSubscriptionAsync(_subscriptionName.ToString(), _inputTopic.ToString(), null, 30, null);
                        //wait after subscription is created - because some delay on google's side
                        await Task.Delay(5000);
                        Log.Info("Created subscription {sub} for topic {topic}", _subscriptionName.ToString(), _inputTopic.ToString());
                        break;
                    }
                    catch (RpcException ex1) when (ex1.StatusCode == StatusCode.NotFound)
                    {
                        Log.Warn("Failed creating subscription {sub} for topic {topic} {times}", _subscriptionName.ToString(), _inputTopic.ToString(), retries + 1);
                        retries++;
                        if (retries == maxRetries)
                            throw new RebusApplicationException($"Could not create subscription topic {_inputTopic}");
                        await Task.Delay(1000 * retries);
                    }
                }
            }
        }

        public override async Task<TransportMessage> Receive(ITransactionContext context, CancellationToken cancellationToken)
        {
            if (_subscriberClient == null) return null;

            ReceivedMessage receivedMessage;
            try
            {
                var response = await _subscriberClient.PullAsync(_subscriptionName, returnImmediately: false, maxMessages: 1, CallSettings.FromCancellationToken(cancellationToken));
                receivedMessage = response.ReceivedMessages.FirstOrDefault();
            }
            catch (RpcException ex) when (ex.Status.StatusCode == StatusCode.Unavailable)
            {
                throw new RebusApplicationException(ex, "GooglePubSub UNAVAILABLE due to too many concurrent pull requests pending for the given subscription");

            }
            catch (Exception ex)
            {
                throw new RebusApplicationException(ex, "Failed when fetching messages from GooglePubSub");
            }


            if (receivedMessage == null) return null;

            var receivedTransportMessage = receivedMessage.ToRebusTransportMessage();

            var utcNow = DateTimeOffset.UtcNow;
            if (receivedTransportMessage.IsExpired(utcNow))
            {
                Log.Debug($"Discarded message {string.Join(",", receivedTransportMessage.Headers.Select(a => a.Key + " : " + a.Value).ToArray())} because message expired {receivedTransportMessage.AbsoluteExpiryTimeUtc()} which is lesser than current time {utcNow}");
                return null;
            }


            context.OnCompleted(async ctx =>
            {
                await _subscriberClient.AcknowledgeAsync(_subscriptionName, new[] { receivedMessage.AckId });
            });

            context.OnAborted(async ctx =>
            {
                await _subscriberClient.ModifyAckDeadlineAsync(_subscriptionName, new[] { receivedMessage.AckId }, 0);
            });

            return receivedTransportMessage;
        }



        protected override async Task SendOutgoingMessages(IEnumerable<OutgoingMessage> outgoingMessages, ITransactionContext context)
        {
            var messagesByDestinationQueues = outgoingMessages.GroupBy(m => m.DestinationAddress);

            async Task SendMessagesToQueue(string queueName, IEnumerable<OutgoingMessage> messages)
            {
                var publisherClient = await GetPublisherClient(queueName);

                await Task.WhenAll(
                    messages
                        .Select(m => m.TransportMessage.ToPubSubMessage())
                        .Select(publisherClient.PublishAsync)
                );
            }

            await Task.WhenAll(messagesByDestinationQueues.Select(g => SendMessagesToQueue(g.Key, g)));
        }

        async Task<PublisherClient> GetPublisherClient(string queueName)
        {
            async Task<PublisherClient> CreatePublisherClient()
            {
                var topicName = TopicName.FromProjectTopic(_projectId, queueName);
                try
                {
                    return await PublisherClient.CreateAsync(topicName);
                }
                catch (Exception exception)
                {
                    throw new RebusApplicationException(exception, $"Could not create publisher client for topic {topicName}");
                }
            }

            var task = _clients.GetOrAdd(queueName, _ => new Lazy<Task<PublisherClient>>(CreatePublisherClient));

            return await task.Value;
        }

        public void Dispose()
        {
        }
    }
}