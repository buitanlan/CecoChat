﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using CecoChat.Contracts;
using CecoChat.Contracts.Backplane;
using CecoChat.Contracts.Messaging;
using CecoChat.Kafka;
using CecoChat.Server.Backplane;
using CecoChat.Server.Messaging.Clients.Streaming;
using Confluent.Kafka;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CecoChat.Server.Messaging.Backplane
{
    public interface IReceiversConsumer : IDisposable
    {
        void Prepare(PartitionRange partitions);

        void Start(CancellationToken ct);

        string ConsumerID { get; }
    }

    public sealed class ReceiversConsumer : IReceiversConsumer
    {
        private readonly ILogger _logger;
        private readonly BackplaneOptions _backplaneOptions;
        private readonly ITopicPartitionFlyweight _partitionFlyweight;
        private readonly IKafkaConsumer<Null, BackplaneMessage> _consumer;
        private readonly IClientContainer _clientContainer;
        private readonly IContractDataMapper _mapper;
        private bool _isInitialized;
        private readonly object _initializationLock;

        public ReceiversConsumer(
            ILogger<ReceiversConsumer> logger,
            IOptions<BackplaneOptions> backplaneOptions,
            ITopicPartitionFlyweight partitionFlyweight,
            IFactory<IKafkaConsumer<Null, BackplaneMessage>> consumerFactory,
            IClientContainer clientContainer,
            IContractDataMapper mapper)
        {
            _logger = logger;
            _backplaneOptions = backplaneOptions.Value;
            _partitionFlyweight = partitionFlyweight;
            _consumer = consumerFactory.Create();
            _clientContainer = clientContainer;
            _mapper = mapper;

            _initializationLock = new object();
        }

        public void Dispose()
        {
            _consumer.Dispose();
        }

        public void Prepare(PartitionRange partitions)
        {
            lock (_initializationLock)
            {
                if (!_isInitialized)
                {
                    _consumer.Initialize(_backplaneOptions.Kafka, _backplaneOptions.ReceiversConsumer, new BackplaneMessageDeserializer());
                    _isInitialized = true;
                }
            }

            _consumer.Assign(_backplaneOptions.TopicMessagesByReceiver, partitions, _partitionFlyweight);
        }

        public void Start(CancellationToken ct)
        {
            _logger.LogInformation("Start sending messages to receivers.");

            while (!ct.IsCancellationRequested)
            {
                _consumer.Consume(consumeResult =>
                {
                    ProcessMessage(consumeResult.Message.Value);
                }, ct);
            }

            _logger.LogInformation("Stopped sending messages to receivers.");
        }

        public string ConsumerID => _backplaneOptions.ReceiversConsumer.ConsumerGroupID;

        private void ProcessMessage(BackplaneMessage backplaneMessage)
        {
            Guid senderClientID = backplaneMessage.ClientId.ToGuid();
            IEnumerable<IStreamer<ListenNotification>> receiverClients = _clientContainer.EnumerateClients(backplaneMessage.ReceiverId);

            ListenNotification notification = _mapper.CreateListenNotification(backplaneMessage);
            EnqueueMessage(senderClientID, notification, receiverClients, out int successCount, out int allCount);
            LogResults(backplaneMessage, successCount, allCount);
        }

        private static void EnqueueMessage(
            Guid senderClientID, ListenNotification notification, IEnumerable<IStreamer<ListenNotification>> receiverClients,
            out int successCount, out int allCount)
        {
            // do not call clients.Count since it is expensive and uses locks
            successCount = 0;
            allCount = 0;

            foreach (IStreamer<ListenNotification> receiverClient in receiverClients)
            {
                if (receiverClient.ClientID != senderClientID)
                {
                    if (receiverClient.EnqueueMessage(notification, parentActivity: Activity.Current))
                    {
                        successCount++;
                    }

                    allCount++;
                }
            }
        }

        private void LogResults(BackplaneMessage backplaneMessage, int successCount, int allCount)
        {
            if (successCount < allCount)
            {
                _logger.LogWarning("Connected recipients with ID {0} ({1} out of {2}) were queued message {3}.",
                    backplaneMessage.ReceiverId, successCount, allCount, backplaneMessage.MessageId);
            }
            else if (allCount > 0)
            {
                _logger.LogTrace("Connected recipients with ID {0} (all {1}) were queued message {2}.",
                    backplaneMessage.ReceiverId, successCount, backplaneMessage.MessageId);
            }
        }
    }
}
