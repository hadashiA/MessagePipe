﻿using MessagePipe.Internal;
using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace MessagePipe
{
    [Preserve]
    public sealed class AsyncMessageBroker<TMessage> : IAsyncPublisher<TMessage>, IAsyncSubscriber<TMessage>
    {
        readonly AsyncMessageBrokerCore<TMessage> core;
        readonly FilterAttachedAsyncMessageHandlerFactory handlerFactory;

        [Preserve]
        public AsyncMessageBroker(AsyncMessageBrokerCore<TMessage> core, FilterAttachedAsyncMessageHandlerFactory handlerFactory)
        {
            this.core = core;
            this.handlerFactory = handlerFactory;
        }

        public void Publish(TMessage message, CancellationToken cancellationToken)
        {
            core.Publish(message, cancellationToken);
        }

        public ValueTask PublishAsync(TMessage message, CancellationToken cancellationToken)
        {
            return core.PublishAsync(message, cancellationToken);
        }

        public ValueTask PublishAsync(TMessage message, AsyncPublishStrategy publishStrategy, CancellationToken cancellationToken)
        {
            return core.PublishAsync(message, publishStrategy, cancellationToken);
        }

        public IDisposable Subscribe(IAsyncMessageHandler<TMessage> handler, AsyncMessageHandlerFilter<TMessage>[] filters)
        {
            return core.Subscribe(handlerFactory.CreateAsyncMessageHandler(handler, filters));
        }
    }

    [Preserve]
    public sealed class BufferedAsyncMessageBroker<TMessage> : IBufferedAsyncPublisher<TMessage>, IBufferedAsyncSubscriber<TMessage>
    {
        readonly BufferedAsyncMessageBrokerCore<TMessage> core;
        readonly FilterAttachedAsyncMessageHandlerFactory handlerFactory;

        [Preserve]
        public BufferedAsyncMessageBroker(BufferedAsyncMessageBrokerCore<TMessage> core, FilterAttachedAsyncMessageHandlerFactory handlerFactory)
        {
            this.core = core;
            this.handlerFactory = handlerFactory;
        }

        public void Publish(TMessage message, CancellationToken cancellationToken)
        {
            core.Publish(message, cancellationToken);
        }

        public ValueTask PublishAsync(TMessage message, CancellationToken cancellationToken)
        {
            return core.PublishAsync(message, cancellationToken);
        }

        public ValueTask PublishAsync(TMessage message, AsyncPublishStrategy publishStrategy, CancellationToken cancellationToken)
        {
            return core.PublishAsync(message, publishStrategy, cancellationToken);
        }

        public ValueTask<IDisposable> SubscribeAsync(IAsyncMessageHandler<TMessage> handler, CancellationToken cancellationToken)
        {
            return SubscribeAsync(handler, Array.Empty<AsyncMessageHandlerFilter<TMessage>>(), cancellationToken);
        }

        public ValueTask<IDisposable> SubscribeAsync(IAsyncMessageHandler<TMessage> handler, AsyncMessageHandlerFilter<TMessage>[] filters, CancellationToken cancellationToken)
        {
            handler = handlerFactory.CreateAsyncMessageHandler(handler, filters);
            return core.SubscribeAsync(handler, cancellationToken);
        }
    }

    [Preserve]
    public sealed class BufferedAsyncMessageBrokerCore<TMessage>
    {
        static readonly bool IsValueType = typeof(TMessage).IsValueType;

        readonly AsyncMessageBrokerCore<TMessage> core;
        TMessage? lastMessage;

        [Preserve]
        public BufferedAsyncMessageBrokerCore(AsyncMessageBrokerCore<TMessage> core)
        {
            this.core = core;
            this.lastMessage = default;
        }

        public void Publish(TMessage message, CancellationToken cancellationToken)
        {
            lastMessage = message;
            core.Publish(message, cancellationToken);
        }

        public ValueTask PublishAsync(TMessage message, CancellationToken cancellationToken)
        {
            lastMessage = message;
            return core.PublishAsync(message, cancellationToken);
        }

        public ValueTask PublishAsync(TMessage message, AsyncPublishStrategy publishStrategy, CancellationToken cancellationToken)
        {
            lastMessage = message;
            return core.PublishAsync(message, publishStrategy, cancellationToken);
        }

        public async ValueTask<IDisposable> SubscribeAsync(IAsyncMessageHandler<TMessage> handler, CancellationToken cancellationToken)
        {
            if (IsValueType || lastMessage != null)
            {
                await handler.HandleAsync(lastMessage!, cancellationToken);
            }
            return core.Subscribe(handler);
        }
    }

    [Preserve]
    public sealed class AsyncMessageBrokerCore<TMessage> : IDisposable, IHandlerHolderMarker
    {
        FreeList<IAsyncMessageHandler<TMessage>> handlers;
        readonly MessagePipeDiagnosticsInfo diagnotics;
        readonly AsyncPublishStrategy defaultAsyncPublishStrategy;
        readonly HandlingSubscribeDisposedPolicy handlingSubscribeDisposedPolicy;
        readonly object gate = new object();
        bool isDisposed;

        [Preserve]
        public AsyncMessageBrokerCore(MessagePipeDiagnosticsInfo diagnotics, MessagePipeOptions options)
        {
            this.handlers = new FreeList<IAsyncMessageHandler<TMessage>>();
            this.defaultAsyncPublishStrategy = options.DefaultAsyncPublishStrategy;
            this.handlingSubscribeDisposedPolicy = options.HandlingSubscribeDisposedPolicy;
            this.diagnotics = diagnotics;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Publish(TMessage message, CancellationToken cancellationToken)
        {
            var array = handlers.GetValues();
            for (int i = 0; i < array.Length; i++)
            {
                array[i]?.HandleAsync(message, cancellationToken).Forget();
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ValueTask PublishAsync(TMessage message, CancellationToken cancellationToken)
        {
            return PublishAsync(message, defaultAsyncPublishStrategy, cancellationToken);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public async ValueTask PublishAsync(TMessage message, AsyncPublishStrategy publishStrategy, CancellationToken cancellationToken)
        {
            var array = handlers.GetValues();
            if (publishStrategy == AsyncPublishStrategy.Sequential)
            {
                foreach (var item in array)
                {
                    if (item != null)
                    {
                        await item.HandleAsync(message, cancellationToken);
                    }
                }
            }
            else
            {
                await new AsyncHandlerWhenAll<TMessage>(array, message, cancellationToken);
            }
        }

        public IDisposable Subscribe(IAsyncMessageHandler<TMessage> handler)
        {
            lock (gate)
            {
                if (isDisposed) return handlingSubscribeDisposedPolicy.Handle(nameof(AsyncMessageBrokerCore<TMessage>));

                var subscriptionKey = handlers.Add(handler);
                var subscription = new Subscription(this, subscriptionKey);
                diagnotics.IncrementSubscribe(this, subscription);
                return subscription;
            }
        }

        public void Dispose()
        {
            lock (gate)
            {
                // Dispose is called when scope is finished.
                if (!isDisposed && handlers.TryDispose(out var count))
                {
                    isDisposed = true;
                    diagnotics.RemoveTargetDiagnostics(this, count);
                }
            }
        }

        sealed class Subscription : IDisposable
        {
            bool isDisposed;
            readonly AsyncMessageBrokerCore<TMessage> core;
            readonly int subscriptionKey;

            public Subscription(AsyncMessageBrokerCore<TMessage> core, int subscriptionKey)
            {
                this.core = core;
                this.subscriptionKey = subscriptionKey;
            }

            public void Dispose()
            {
                if (!isDisposed)
                {
                    isDisposed = true;
                    lock (core.gate)
                    {
                        core.handlers.Remove(subscriptionKey, true);
                        core.diagnotics.DecrementSubscribe(core, this);
                    }
                }
            }
        }
    }
}