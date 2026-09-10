using ProtoBuf;

namespace ServerMap.Network;

[ProtoContract] public sealed class ClientMountReadyPacket { }
[ProtoContract] public sealed class ServerMountRequestPacket
{
    [ProtoMember(1)] public string Token { get; set; } = "";
    [ProtoMember(2)] public long EntityId { get; set; }
}
[ProtoContract] public sealed class ClientMountChunkPacket
{
    [ProtoMember(1)] public string Token { get; set; } = "";
    [ProtoMember(2)] public long EntityId { get; set; }
    [ProtoMember(3)] public int Index { get; set; }
    [ProtoMember(4)] public int Total { get; set; }
    [ProtoMember(5)] public byte[] Data { get; set; } = [];
    [ProtoMember(6)] public double WorldSize { get; set; }
    [ProtoMember(7)] public double CenterX { get; set; }
    [ProtoMember(8)] public double CenterZ { get; set; }
    [ProtoMember(9)] public string Error { get; set; } = "";
    [ProtoMember(10)] public double Footprint { get; set; }
}
