using ProtoBuf;

namespace ServerMap.Network;

/// <summary>Server-to-client request for the current seasonal color table.</summary>
[ProtoContract]
public sealed class ServerColormapRequestPacket
{
    [ProtoMember(1)] public int Month { get; set; }
}

/// <summary>One compressed client-generated colormap transfer chunk.</summary>
[ProtoContract]
public sealed class ClientColormapChunkPacket
{
    [ProtoMember(1)] public string TransferId { get; set; } = "";
    [ProtoMember(2)] public int ChunkIndex { get; set; }
    [ProtoMember(3)] public int TotalChunks { get; set; }
    [ProtoMember(4)] public byte[] Data { get; set; } = [];
    [ProtoMember(5)] public int Month { get; set; }
}
