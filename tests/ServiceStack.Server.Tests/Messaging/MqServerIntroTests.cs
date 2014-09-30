﻿using System;
using Funq;
using NUnit.Framework;
using ServiceStack.Messaging;
using ServiceStack.Messaging.Redis;
using ServiceStack.RabbitMq;
using ServiceStack.Redis;
using ServiceStack.Testing;
using ServiceStack.Text;

namespace ServiceStack.Server.Tests.Messaging
{
    public class RabbitMqServerIntroTests : MqServerIntroTests
    {
        public override IMessageService CreateMqServer(int retryCount = 1)
        {
            return new RabbitMqServer { RetryCount = retryCount };
        }
    }

    public class RedisMqServerIntroTests : MqServerIntroTests
    {
        public override IMessageService CreateMqServer(int retryCount = 1)
        {
            return new RedisMqServer(new BasicRedisClientManager()) { RetryCount = retryCount };
        }
    }

    public class InMemoryMqServerIntroTests : MqServerIntroTests
    {
        public override IMessageService CreateMqServer(int retryCount = 1)
        {
            return new InMemoryTransientMessageService { RetryCount = retryCount };
        }
    }

    public class HelloIntro
    {
        public string Name { get; set; }
    }

    public class HelloIntroResponse
    {
        public string Result { get; set; }
    }

    public class HelloService : Service
    {
        public object Any(HelloIntro request)
        {
            return new HelloIntroResponse { Result = "Hello, {0}!".Fmt(request.Name) };
        }
    }

    public class AppHost : AppSelfHostBase
    {
        private readonly Func<IMessageService> createMqServerFn;

        public AppHost(Func<IMessageService> createMqServerFn)
            : base("Rabbit MQ Test Host", typeof (HelloService).Assembly)
        {
            this.createMqServerFn = createMqServerFn;
        }

        public override void Configure(Container container)
        {
            container.Register(c => createMqServerFn());

            var mqServer = container.Resolve<IMessageService>();

            mqServer.RegisterHandler<HelloIntro>(ServiceController.ExecuteMessage);
            mqServer.Start();
        }
    }

    [TestFixture]
    public abstract class MqServerIntroTests
    {
        public abstract IMessageService CreateMqServer(int retryCount = 1);

        [Test]
        public void Messages_with_no_responses_are_published_to_Request_outq_topic()
        {
            using (var mqServer = CreateMqServer())
            {
                mqServer.RegisterHandler<HelloIntro>(m =>
                {
                    "Hello, {0}!".Print(m.GetBody().Name);
                    return null;
                });
                mqServer.Start();

                using (var mqClient = mqServer.CreateMessageQueueClient())
                {
                    mqClient.Publish(new HelloIntro { Name = "World" });

                    IMessage<HelloIntro> msgCopy = mqClient.Get<HelloIntro>(QueueNames<HelloIntro>.Out);
                    mqClient.Ack(msgCopy);
                    Assert.That(msgCopy.GetBody().Name, Is.EqualTo("World"));
                }
            }
        }

        [Test]
        public void Message_with_response_are_published_to_Response_inq()
        {
            using (var mqServer = CreateMqServer())
            {
                mqServer.RegisterHandler<HelloIntro>(m =>
                    new HelloIntroResponse { Result = "Hello, {0}!".Fmt(m.GetBody().Name) });
                mqServer.Start();

                using (var mqClient = mqServer.CreateMessageQueueClient())
                {
                    mqClient.Publish(new HelloIntro { Name = "World" });

                    IMessage<HelloIntroResponse> responseMsg = mqClient.Get<HelloIntroResponse>(QueueNames<HelloIntroResponse>.In);
                    mqClient.Ack(responseMsg);
                    Assert.That(responseMsg.GetBody().Result, Is.EqualTo("Hello, World!"));
                }
            }
        }

        [Test]
        public void Message_with_exceptions_are_retried_then_published_to_Request_dlq()
        {
            using (var mqServer = CreateMqServer(retryCount:1))
            {
                var called = 0;
                mqServer.RegisterHandler<HelloIntro>(m =>
                {
                    called++;
                    throw new ArgumentException("Name");
                });
                mqServer.Start();

                using (var mqClient = mqServer.CreateMessageQueueClient())
                {
                    mqClient.Publish(new HelloIntro { Name = "World" });

                    IMessage<HelloIntro> dlqMsg = mqClient.Get<HelloIntro>(QueueNames<HelloIntro>.Dlq);
                    mqClient.Ack(dlqMsg);

                    Assert.That(called, Is.EqualTo(2));
                    Assert.That(dlqMsg.GetBody().Name, Is.EqualTo("World"));
                    Assert.That(dlqMsg.Error.ErrorCode, Is.EqualTo(typeof(ArgumentException).Name));
                    Assert.That(dlqMsg.Error.Message, Is.EqualTo("Name"));
                }
            }
        }

        [Test]
        public void Message_with_ReplyTo_are_published_to_the_ReplyTo_queue()
        {
            using (var mqServer = CreateMqServer())
            {
                mqServer.RegisterHandler<HelloIntro>(m =>
                    new HelloIntroResponse { Result = "Hello, {0}!".Fmt(m.GetBody().Name) });
                mqServer.Start();

                using (var mqClient = mqServer.CreateMessageQueueClient())
                {
                    const string replyToMq = "mq:Hello.replyto";
                    mqClient.Publish(new Message<HelloIntro>(new HelloIntro { Name = "World" }) {
                        ReplyTo = replyToMq
                    });

                    IMessage<HelloIntroResponse> responseMsg = mqClient.Get<HelloIntroResponse>(replyToMq);
                    mqClient.Ack(responseMsg);
                    Assert.That(responseMsg.GetBody().Result, Is.EqualTo("Hello, World!"));
                }
            }
        }

        [Test]
        public void Does_process_messages_in_HttpListener_AppHost()
        {
            using (var appHost = new AppHost(() => CreateMqServer()).Init())
            {
                using (var mqClient = appHost.Resolve<IMessageService>().CreateMessageQueueClient())
                {
                    mqClient.Publish(new HelloIntro { Name = "World" });

                    IMessage<HelloIntroResponse> responseMsg = mqClient.Get<HelloIntroResponse>(QueueNames<HelloIntroResponse>.In);
                    mqClient.Ack(responseMsg);
                    Assert.That(responseMsg.GetBody().Result, Is.EqualTo("Hello, World!"));
                }
            }
        }

        [Test]
        public void Does_process_messages_in_BasicAppHost()
        {
            using (var appHost = new BasicAppHost(typeof(HelloService).Assembly)
            {
                ConfigureAppHost = host =>
                {
                    host.Container.Register(c => CreateMqServer());

                    var mqServer = host.Container.Resolve<IMessageService>();

                    mqServer.RegisterHandler<HelloIntro>(host.ServiceController.ExecuteMessage);
                    mqServer.Start();
                }
            }.Init())
            {
                using (var mqClient = appHost.Resolve<IMessageService>().CreateMessageQueueClient())
                {
                    mqClient.Publish(new HelloIntro { Name = "World" });

                    IMessage<HelloIntroResponse> responseMsg = mqClient.Get<HelloIntroResponse>(QueueNames<HelloIntroResponse>.In);
                    mqClient.Ack(responseMsg);
                    Assert.That(responseMsg.GetBody().Result, Is.EqualTo("Hello, World!"));
                }
            }
        }
    }

    public class RabbitMqServerPostMessageTests : MqServerPostMessageTests
    {
        public override IMessageService CreateMqServer(IAppHost host, int retryCount = 1)
        {
            return new RabbitMqServer
            {
                RetryCount = retryCount,
                ResponseFilter = r => { host.OnEndRequest(); return r; }
            };
        }
    }

    public class RedisMqServerPostMessageTests : MqServerPostMessageTests
    {
        public override IMessageService CreateMqServer(IAppHost host, int retryCount = 1)
        {
            return new RedisMqServer(new BasicRedisClientManager())
            {
                RetryCount = retryCount,
                ResponseFilter = r => { host.OnEndRequest(); return r; }
            };
        }
    }

    public class HelloIntroWithDep
    {
        public string Name { get; set; }
    }

    public class HelloWithDepService : Service
    {
        public IDisposableDependency Dependency { get; set; }

        public object Any(HelloIntroWithDep request)
        {
            return new HelloIntroResponse { Result = "Hello, {0}!".Fmt(request.Name) };
        }
    }

    public interface IDisposableDependency : IDisposable
    {

    }

    public class DisposableDependency : IDisposableDependency
    {
        private readonly Action onDispose;

        public DisposableDependency(Action onDispose)
        {
            this.onDispose = onDispose;
        }

        public void Dispose()
        {
            if (this.onDispose != null)
                this.onDispose();
        }
    }

    [TestFixture]
    public abstract class MqServerPostMessageTests
    {
        public abstract IMessageService CreateMqServer(IAppHost host, int retryCount = 1);

        [Test]
        public void Does_dispose_request_scope_dependency_in_PostMessageHandler()
        {
            var disposeCount = 0;
            using (var appHost = new BasicAppHost(typeof(HelloWithDepService).Assembly)
            {
                ConfigureAppHost = host =>
                {
                    RequestContext.UseThreadStatic = true;
                    host.Container.Register<IDisposableDependency>(c => new DisposableDependency(() =>
                    {
                        disposeCount++;
                    }))
                        .ReusedWithin(ReuseScope.Request);
                    host.Container.Register(c => CreateMqServer(host));

                    var mqServer = host.Container.Resolve<IMessageService>();

                    mqServer.RegisterHandler<HelloIntroWithDep>(host.ServiceController.ExecuteMessage);
                    mqServer.Start();
                }
            }.Init())
            {
                using (var mqClient = appHost.Resolve<IMessageService>().CreateMessageQueueClient())
                {
                    mqClient.Publish(new HelloIntroWithDep { Name = "World" });

                    IMessage<HelloIntroResponse> responseMsg = mqClient.Get<HelloIntroResponse>(QueueNames<HelloIntroResponse>.In);
                    mqClient.Ack(responseMsg);

                    Assert.That(disposeCount, Is.EqualTo(1));
                }
            }
        }
    }
}