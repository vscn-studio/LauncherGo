using ProtoBuf;

namespace ServerMap.Network;

[ProtoContract] public sealed class ClientMapReadyPacket
{
    [ProtoMember(1)] public int ExplorationProtocol { get; set; }
    [ProtoMember(2)] public string ExplorationClient { get; set; } = "";
}
[ProtoContract] public sealed class ServerHiddenMapPacket
{
    // Flattened minX/minZ/maxX/maxZ; no private region names are transmitted.
    [ProtoMember(1)] public double[] Bounds { get; set; } = [];
    [ProtoMember(2)] public int ExplorationProtocol { get; set; }
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

[ProtoContract] public sealed class ClientMapExplorationPacket
{
    [ProtoMember(1)] public string Session { get; set; } = "";
    [ProtoMember(2)] public long Sequence { get; set; }
    [ProtoMember(3)] public long[] Cells { get; set; } = [];
    [ProtoMember(4)] public int Dimension { get; set; }
}

[ProtoContract] public sealed class ServerMapExplorationAckPacket
{
    [ProtoMember(1)] public string Session { get; set; } = "";
    // Zero is the initial hello, positive sequences acknowledge durably stored batches.
    [ProtoMember(2)] public long Sequence { get; set; }
    [ProtoMember(3)] public long[] Accepted { get; set; } = [];
    [ProtoMember(4)] public string ClientSession { get; set; } = "";
}
