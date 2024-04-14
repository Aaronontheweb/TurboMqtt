// -----------------------------------------------------------------------
// <copyright file="Mqtt311DecoderSpecs.cs" company="Petabridge, LLC">
//      Copyright (C) 2024 - 2024 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------

using TurboMqtt.Core.PacketTypes;
using TurboMqtt.Core.Protocol;
using TurboMqtt.Core.Tests.Packets;

namespace TurboMqtt.Core.Tests.Protocol;

public class Mqtt311DecoderSpecs
{
    [Theory]
    [InlineData(new byte[] { 0x00 }, 0)]  // Just one byte needed for length 0
    [InlineData(new byte[] { 0x01 }, 1)]  // Correct single byte encoding
    [InlineData(new byte[] { 0x7F }, 127)]  // Single byte for 127
    [InlineData(new byte[] { 0x80, 0x01 }, 128)]  // Correct encoding for 128 (continuation bit set)
    [InlineData(new byte[] { 232, 0x07 }, 1000)]
    [InlineData(new byte[] { 0x80, 0x80, 0x01 }, 16384)]
    [InlineData(new byte[] { 0xD0, 0x86, 0x03 }, 50000)]
    [InlineData(new byte[] { 0x80, 0x80, 0x80, 0x01 }, 2097152)]
    [InlineData(new byte[] { 128, 173, 226, 4 }, 10000000)]
    public void ShouldParseValidFrameLengthHeader(byte[] header, int expectedLength)
    {
        var span = new ReadOnlySpan<byte>(header);
        var foundLength = Mqtt311Decoder.TryGetPacketLength(ref span, out var bodyLength);
        Assert.True(foundLength);
        Assert.Equal(expectedLength, bodyLength);
    }

    public class CanHandlePartialMessages
    {
        private readonly Mqtt311Decoder _decoder = new();

        [Fact]
        public void ShouldHandlePartialFrameHeader()
        {
            var buffer = new byte[] { 0x80 };
            var span = new ReadOnlySpan<byte>(buffer);
            var foundLength = Mqtt311Decoder.TryGetPacketLength(ref span, out var bodyLength);
            Assert.False(foundLength);
        }
        
            [Fact]
            public void ShouldHandlePartialFrameBody()
            {
                // arrange
                var publishPacket = new PublishPacket(QualityOfService.AtLeastOnce, false, false, "topic1")
                {
                    PacketId = 1,
                    Payload = new byte[] { 0x01, 0x02, 0x03 }
                };

                var encodedPacket = PacketEncodingTestHelper.EncodePacketOnly(publishPacket);
                
                // split the packet into two frames
                ReadOnlyMemory<byte> frame1 = encodedPacket[..^1];
                ReadOnlyMemory<byte> frame2 = encodedPacket[^1..];
                
                // act
                var decodedPacket1 = _decoder.TryDecode(in frame1, out var packets1);
                decodedPacket1.Should().BeFalse();
                packets1.Should().BeEmpty();
                
                var decodedPacket2 = _decoder.TryDecode(in frame2, out var packets2);
                decodedPacket2.Should().BeTrue();
                packets2.Count.Should().Be(1);
                
                // assert
                packets2[0].Should().BeEquivalentTo(publishPacket, options => options.Excluding(x => x.Payload));
            }

            [Fact]
            public void ShouldHandleMultipleMessages()
            {
                // arrange
                var publishPacket1 = new PublishPacket(QualityOfService.AtLeastOnce, false, false, "topic1")
                {
                    PacketId = 1,
                    Payload = new byte[] { 0x01, 0x02, 0x03 }
                };
                
                var publishPacket2 = new PublishPacket(QualityOfService.AtLeastOnce, false, false, "topic2")
                {
                    PacketId = 2,
                    Payload = new byte[] { 0x04, 0x05, 0x06 }
                };

                var pingRespPacket = PingRespPacket.Instance;
                
                var publishPacket3 = new PublishPacket(QualityOfService.AtLeastOnce, false, false, "topic3")
                {
                    PacketId = 3,
                    Payload = new byte[] { 0x07, 0x08, 0x09 }
                };
                
                var packets = new List<MqttPacket>
                {
                    publishPacket1,
                    publishPacket2,
                    pingRespPacket,
                    publishPacket3
                };
                
                // estimate size of all packets
                var packetsAndSizes = packets.Select(c => (c, MqttPacketSizeEstimator.EstimateMqtt3PacketSize(c))).ToArray();
                var totalSize = packetsAndSizes.Sum(x => x.Item2) + packetsAndSizes.Length*2;
                var buffer = new Memory<byte>(new byte[totalSize]);
                
                // encode all packets
                var bytesWritten = Mqtt311Encoder.EncodePackets(packetsAndSizes, ref buffer);
                bytesWritten.Should().Be(totalSize);
                
                // act
                var decoded = _decoder.TryDecode(buffer, out var decodedPackets);
                
                // assert
                decoded.Should().BeTrue();
                decodedPackets.Count.Should().Be(4);
                
                decodedPackets[0].Should().BeEquivalentTo(publishPacket1, options => options.Excluding(x => x.Payload));
                decodedPackets[1].Should().BeEquivalentTo(publishPacket2, options => options.Excluding(x => x.Payload));
                decodedPackets[2].Should().BeEquivalentTo(pingRespPacket);
                decodedPackets[3].Should().BeEquivalentTo(publishPacket3, options => options.Excluding(x => x.Payload));
            }
    }
}