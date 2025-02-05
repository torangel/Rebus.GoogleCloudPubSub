﻿using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Rebus.Activation;
using Rebus.Bus;
using Rebus.Config;
using Rebus.Logging;
using Rebus.Tests.Contracts.Transports;

namespace Rebus.GoogleCloudPubSub.Tests.Factory
{
    public class GoogleCloudPubSubBusFactory : IBusFactory
    {
        readonly List<IDisposable> _stuffToDispose = new();

        public IBus GetBus<TMessage>(string inputQueueAddress, Func<TMessage, Task> handler)
        {
            var builtinHandlerActivator = new BuiltinHandlerActivator();

            builtinHandlerActivator.Handle(handler);

            PurgeQueue(inputQueueAddress);

            var bus = Configure.With(builtinHandlerActivator)
                .Transport(t => t.UsePubSub(inputQueueAddress))
                .Options(o =>
                {
                    o.SetNumberOfWorkers(10);
                    o.SetMaxParallelism(10);
                })
                .Start();

            _stuffToDispose.Add(bus);

            return bus;
        }

        static void PurgeQueue(string queueName)
        {
            var consoleLoggerFactory = new ConsoleLoggerFactory(false);

            using var transport = new GoogleCloudPubSubTransport(GoogleCredentials.GetGoogleCredentialsFromEnvironmentVariable().ProjectId, queueName, consoleLoggerFactory);

            transport.PurgeQueueAsync()
                .GetAwaiter()
                .GetResult();
        }

        public void Cleanup()
        {
            _stuffToDispose.ForEach(d => d.Dispose());
            _stuffToDispose.Clear();
        }
    }
}