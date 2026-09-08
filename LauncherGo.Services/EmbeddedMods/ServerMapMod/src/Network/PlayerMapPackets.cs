using ProtoBuf;

namespace ServerMap.Network;

[ProtoContract] public sealed class ClientMapReadyPacket { }
[ProtoContract] public sealed class ServerHiddenMapPacket
{
    // Flattened minX/minZ/maxX/maxZ; no private region names are transmitted.
    [ProtoMember(1)] public double[] Bounds { get; set; } = [];
}
[ProtoContract] public sealed class ServerAvatarRequestPacket
{
    [ProtoMember(1)] public string Token { get; set; } = "";
    [ProtoMember(2)] public string Appearance { get; set; } = "";
}
[ProtoContract] public sealed class ClientAvatarChunkPacket
{
    [ProtoMember(1)] public string Token { get; set; } = "";
    [ProtoMember(2)] public int Index { get; set; }
    [ProtoMember(3)] public int Total { get; set; }
    [ProtoMember(4)] public byte[] Data { get; set; } = [];
    // A token-bound failure report, so the server does not keep showing "waiting".
    [ProtoMember(5)] public string Error { get; set; } = "";
}
