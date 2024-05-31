﻿// -----------------------------------------------------------------------
// <copyright file="TlsMqtt311End2EndSpecs.cs" company="Petabridge, LLC">
//      Copyright (C) 2024 - 2024 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------

using System.Buffers;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Akka.Configuration;
using Akka.Event;
using TurboMqtt.Client;
using TurboMqtt.IO;
using TurboMqtt.IO.Tcp;
using TurboMqtt.PacketTypes;
using TurboMqtt.Protocol;
using Xunit.Abstractions;

namespace TurboMqtt.Tests.End2End;

public class TlsMqtt311End2EndSpecs : TransportSpecBase
{
    private static readonly Config DebugLogging = """
                                                  akka.loglevel = DEBUG
                                                  """;

    // This is a workaround for this issue:
    // https://github.com/dotnet/runtime/issues/23749
    private static readonly X509Certificate2 RootCert = new (
        X509Certificate2.CreateFromEncryptedPemFile("./certs/root_cert.pem", "password")
            .Export(X509ContentType.Pkcs12));

    private static readonly X509ChainPolicy RootChainPolicy = new()
    {
        CustomTrustStore = { RootCert },
        TrustMode = X509ChainTrustMode.CustomRootTrust, 
        RevocationMode = X509RevocationMode.NoCheck
    };

    private static readonly X509Chain RootChain = new ()
    {
        ChainPolicy = RootChainPolicy
    };

    private readonly FakeMqttTcpServer _server;
    
    public TlsMqtt311End2EndSpecs(ITestOutputHelper output) : base(output: output, config: DebugLogging)
    {
        var logger = new BusLogging(Sys.EventStream, "FakeMqttTcpServer", typeof(FakeMqttTcpServer),
            Sys.Settings.LogFormatter);

        _server = new FakeMqttTcpServer(DefaultTcpServerOptions(), MqttProtocolVersion.V3_1_1,
            logger, TimeSpan.Zero, new DefaultFakeServerHandleFactory());
        _server.Bind();
    }
    
    // This is a workaround for this issue:
    // https://github.com/dotnet/runtime/issues/23749
    private static readonly X509Certificate2 ServerCert =  new X509Certificate2(
        X509Certificate2.CreateFromEncryptedPemFile("./certs/server_cert.pem", "password")
            .Export(X509ContentType.Pkcs12));
    
    private MqttTcpServerOptions DefaultTcpServerOptions() => new ("localhost", 21883)
    {
        SslOptions = new SslServerAuthenticationOptions
        {
            ServerCertificate = ServerCert,
            ClientCertificateRequired = false,
        }, 
        ClientCertificateValidator = ValidateClientCertificate
    };
    
    private bool ValidateClientCertificate(
        object sender,
        X509Certificate? certificate,
        X509Chain? chain,
        SslPolicyErrors sslPolicyErrors)
    {
        if (sslPolicyErrors == SslPolicyErrors.None)
            return true;

        // Return true if client certificate is not required
        if (sslPolicyErrors == SslPolicyErrors.RemoteCertificateNotAvailable)
            return true;
        
        // Validate client certificate with a custom chain
        if (certificate is not null)
        {
            var isValid = RootChain.Build(new X509Certificate2(certificate));
            if (!isValid)
            {
                foreach (var status in RootChain.ChainStatus)
                {
                    Log.Error("[Server] Chain error: {0}", status.StatusInformation);
                }
            }

            return isValid;
        }

        // Refuse everything else
        Log.Error("[Server] Certificate error: {0}", sslPolicyErrors);
        return false;
    }    

    private bool ValidateServerCertificate(
        object sender,
        X509Certificate? certificate,
        X509Chain? chain,
        SslPolicyErrors errors)
    {
        if (errors == SslPolicyErrors.None)
            return true;

        // Missing cert or the destination hostname wasn't valid for the cert.
        if ((errors & ~SslPolicyErrors.RemoteCertificateChainErrors) != 0)
            return false;

        // Validate client certificate with a custom chain
        if (certificate is not null)
        {
            chain ??= RootChain;
            var isValid = chain.Build(new X509Certificate2(certificate));
            if (!isValid)
            {
                foreach (var status in chain.ChainStatus)
                {
                    Log.Error("[Client] Chain error: [{0}] {1}", status.Status, status.StatusInformation);
                }
            }

            return isValid;
        }
        
        // Refuse everything else
        Log.Error("[Client] Certificate error: {0}", errors);
        return false;
    }
    
    public override async Task<IMqttClient> CreateClient()
    {
        var client = await ClientFactory.CreateTcpClient(DefaultConnectOptions, DefaultTcpOptions);
        return client;
    }

    private MqttClientTcpOptions DefaultTcpOptions => new("localhost", 21883)
    {
        TlsOptions = new ClientTlsOptions
        {
            UseTls = true,
            SslOptions = new SslClientAuthenticationOptions
            {
                TargetHost = "localhost",
                CertificateChainPolicy = RootChainPolicy
            }, 
            ServerCertificateValidator = ValidateServerCertificate
        }
    };

    protected override void AfterAll()
    {
        // shut down our local TCP server
        _server.Shutdown();
        base.AfterAll();
    }

    [Fact]
    public async Task ShouldAutomaticallyReconnectAndSubscribeAfterServerDisconnect()
    {
        var client = await ClientFactory.CreateTcpClient(DefaultConnectOptions, DefaultTcpOptions);

        using var cts = new CancellationTokenSource(RemainingOrDefault);
        var connectResult = await client.ConnectAsync(cts.Token);
        connectResult.IsSuccess.Should().BeTrue();

        // subscribe
        var subResult = await client.SubscribeAsync(DefaultTopic, QualityOfService.AtLeastOnce, cts.Token);
        subResult.IsSuccess.Should().BeTrue();

        // kick the client
        _server.TryKickClient(DefaultConnectOptions.ClientId).Should().BeTrue();

        // automatic reconnect should be happening behind the scenes - attempt to publish a message we will receive
        var mqttMessage = new MqttMessage(DefaultTopic, "hello, world!") { QoS = QualityOfService.AtLeastOnce };
        var pubResult = await client.PublishAsync(mqttMessage);
        pubResult.IsSuccess.Should()
            .BeTrue(
                $"Expected to be able to publish message {mqttMessage} after reconnect, but got {pubResult} instead.");

        // now we should receive the message
        (await client.ReceivedMessages.WaitToReadAsync()).Should().BeTrue();
        client.ReceivedMessages.TryRead(out var receivedMessage).Should().BeTrue();
        receivedMessage!.Topic.Should().Be(DefaultTopic);

        // shut down
        using var shutdownCts = new CancellationTokenSource(RemainingOrDefault);
        await client.DisconnectAsync(shutdownCts.Token);

        await client.WhenTerminated.WaitAsync(shutdownCts.Token);
    }

    [Fact]
    public async Task ShouldTerminateClientAfterMultipleFailedConnectionAttempts()
    {
        // allow 1 reconnection attempt
        var updatedOptions = DefaultConnectOptions with { MaxReconnectAttempts = 1 };
        var client = await ClientFactory.CreateTcpClient(updatedOptions, DefaultTcpOptions);

        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var connectResult = await client.ConnectAsync(cts.Token);
        connectResult.IsSuccess.Should().BeTrue();

        // subscribe
        var subResult = await client.SubscribeAsync(DefaultTopic, QualityOfService.AtLeastOnce, cts.Token);
        subResult.IsSuccess.Should().BeTrue();

        // shutdown server
        _server.Shutdown();

        // wait for retry-->reconnect loop to fail twice

        await client.WhenTerminated.WaitAsync(cts.Token);
        client.WhenTerminated.IsCompleted.Should().BeTrue();
    }
    
    // test case where we attempt to connect to non-existent server. ConnectAsync should fail
    [Fact]
    public async Task ShouldFailToConnectToNonExistentServer()
    {
        var updatedTcpOptions = DefaultTcpOptions with { Port = 21884, MaxReconnectAttempts = 0 };
        var client = await ClientFactory.CreateTcpClient(DefaultConnectOptions, updatedTcpOptions);
        
        // we are going to do this, intentionally, without a CTS here - this operation MUST FAIL if we are unable to connect
        var connectResult = await client.ConnectAsync();
        connectResult.IsSuccess.Should().BeFalse();

        client.IsConnected.Should().BeFalse();
    }

    private sealed class DisconnectOnConnectFakeServerHandler: FakeMqtt311ServerHandle
    {
        private readonly Action<ConnectPacket> _onConnectCallback;
        
        public DisconnectOnConnectFakeServerHandler(
            Action<ConnectPacket> onConnectCallback,
            Func<(IMemoryOwner<byte> buffer, int estimatedSize), bool> pushMessage, 
            Func<Task> closingAction,
            ILoggingAdapter log,
            TimeSpan? heartbeatDelay = null) 
            : base(pushMessage, closingAction, log, heartbeatDelay)
        {
            _onConnectCallback = onConnectCallback;
        }

        public override void HandlePacket(MqttPacket packet)
        {
            if (packet.PacketType == MqttPacketType.Connect)
            {
                var connect = (ConnectPacket)packet;
                ClientIdAssigned.TrySetResult(connect.ClientId);
                _onConnectCallback(connect);
                return;
            }
            base.HandlePacket(packet);
        }
    }
    
    private sealed class ConfigurableFakeServerFactory: IFakeServerHandleFactory
    {
        private readonly Func<Func<(IMemoryOwner<byte> buffer, int estimatedSize), bool>, Func<Task>, ILoggingAdapter,
            TimeSpan?, IFakeServerHandle> _onCreateHandlerCallback;

        public ConfigurableFakeServerFactory(
            Func<Func<(IMemoryOwner<byte> buffer, int estimatedSize), bool>, Func<Task>, ILoggingAdapter,
                TimeSpan?, IFakeServerHandle> onCreateHandlerCallback)
        {
            _onCreateHandlerCallback = onCreateHandlerCallback;
        }

        public IFakeServerHandle CreateServerHandle(
            Func<(IMemoryOwner<byte> buffer, int estimatedSize), bool> pushMessage, 
            Func<Task> closingAction,
            ILoggingAdapter log,
            MqttProtocolVersion protocolVersion = MqttProtocolVersion.V3_1_1, 
            TimeSpan? heartbeatDelay = null)
        {
            return _onCreateHandlerCallback(pushMessage, closingAction, log, heartbeatDelay);
        }
    }
    
    /// <summary>
    /// This is an edge case when the client tries to reconnect and the socket disconnected right after the CONNECT
    /// packet was sent by the client but before the CONNACK packet were received by the client.
    ///
    /// The bug was that the ClientAcksActor were stuck waiting for the previous connection state to complete, blocking
    /// immediate client reconnect attempt.
    ///
    /// There should be 3 connection attempts in this test, with these steps happening in sequence:
    /// 
    /// 1. Client connects normally to the broker
    /// 2. Client connected successfully to the broker
    /// 3. Socket connection lost (forcefully)
    /// 4. Client tries to reconnect to the broker
    /// 5. Client socket connected to the broker and sends a CONNECT packet, ClientAcksActor _pendingConnect field is set
    /// 6. Socket connection lost (forcefully)
    /// 7. ClientAcksActor _pendingConnect field is reset by a Reconnect message
    /// 8. Client tries to reconnect to the broker
    /// 9. Client connected successfully to the broker
    /// </summary>
    [Fact]
    public async Task ShouldReconnectSuccessfullyIfReconnectFlowFailed()
    {
        var connectAttempts = 0;
        var connectPacketTcs = new TaskCompletionSource<ConnectPacket>();
        
        // need our own server
        _server.Shutdown();
        
        var server = new FakeMqttTcpServer(
            options: DefaultTcpServerOptions(), 
            version: MqttProtocolVersion.V3_1_1,
            log: Log,
            heartbeatDelay: TimeSpan.Zero,
            handleFactory: new ConfigurableFakeServerFactory(OnCreateHandlerCallback));
        server.Bind();
        
        var client = await ClientFactory.CreateTcpClient(DefaultConnectOptions, DefaultTcpOptions);
        
        try
        {
            using var cts = new CancellationTokenSource(RemainingOrDefault);
            
            // First connection should succeed
            var connectResult = await client.ConnectAsync(cts.Token);
            connectResult.IsSuccess.Should().BeTrue();

            // Disconnect the client socket forcefully to force it to reconnect
            server.TryDisconnectClientSocket(client.ClientId);

            // Wait for connect packet to arrive in the handler
            await connectPacketTcs.Task;
            
            // Disconnect the client as soon as its client id is registered but without replying with a ConectAck
            await AwaitConditionAsync(() => server.TryDisconnectClientSocket(client.ClientId), cts.Token);
            
            // Client should reconnect even with the dirty _pendingConnect in the ClientAcksActor
            await AwaitConditionAsync(() => client.IsConnected, cts.Token);
        }
        finally
        {
            try
            {
                await client.DisconnectAsync();
            }
            catch
            {
                // no-op
            }
            server.Shutdown();
        }

        return;

        IFakeServerHandle OnCreateHandlerCallback(
            Func<(IMemoryOwner<byte> buffer, int estimatedSize), bool> pushMessage,
            Func<Task> closingAction,
            ILoggingAdapter log,
            TimeSpan? heartbeatDelay)
        {
            connectAttempts++;
            Log.Info($"OnCreateHandlerCallback {connectAttempts}");
            return connectAttempts switch
            {
                1 => new FakeMqtt311ServerHandle(pushMessage, closingAction, log, heartbeatDelay),
                2 => new DisconnectOnConnectFakeServerHandler(OnConnectCallback, pushMessage, closingAction, log,
                    heartbeatDelay),
                _ => new FakeMqtt311ServerHandle(pushMessage, closingAction, log, heartbeatDelay)
            };
        }
        
        void OnConnectCallback(ConnectPacket connect)
        {
            Log.Info($"OnConnectCallback {connectAttempts}");
            connectPacketTcs.SetResult(connect);
        }
    }    
    
    [Fact]
    public async Task ShouldSuccessFullyConnectWhenBrokerAvailableAfterFailedConnectionAttempt()
    {
        var updatedTcpOptions = DefaultTcpOptions with { Port = 21889, MaxReconnectAttempts = 0 };
        var client = await ClientFactory.CreateTcpClient(DefaultConnectOptions, updatedTcpOptions);
        
        // we are going to do this, intentionally, without a CTS here - this operation MUST FAIL if we are unable to connect
        var connectResult = await client.ConnectAsync();
        connectResult.IsSuccess.Should().BeFalse();

        client.IsConnected.Should().BeFalse();

        var updatedServerOptions = DefaultTcpServerOptions() with { Port = 21889 };
        // start up a new server
        var newServer = new FakeMqttTcpServer(updatedServerOptions, MqttProtocolVersion.V3_1_1,
            Sys.Log, TimeSpan.Zero, new DefaultFakeServerHandleFactory());
        try
        {
            newServer.Bind();

            // now we should be able to connect
            var connectResult2 = await client.ConnectAsync();
            connectResult2.IsSuccess.Should().BeTrue();
            
            client.IsConnected.Should().BeTrue();
            await client.DisconnectAsync();

            // ReSharper disable once MethodSupportsCancellation
            await AwaitAssertAsync(() => client.WhenTerminated.IsCompleted.Should().BeTrue());
        }
        finally
        {
            newServer.Shutdown();
        }
    }
}