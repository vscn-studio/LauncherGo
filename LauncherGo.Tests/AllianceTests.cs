using ServerMap.Web;
using Xunit;

namespace LauncherGo.Tests;

public sealed class AllianceTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "launchergo-alliance-tests-" + Guid.NewGuid().ToString("N"));
    [Fact]
    public void MapIdsAreUniquePersistentWorldScopedAndDoNotExposeGameUid()
    {
        using var store=new AllianceStore(root);
        var alice=store.Snapshot("alice");var bob=store.Snapshot("bob");
        Assert.Matches("^[0-9a-f]{32}$",alice.MapId);Assert.NotEqual(alice.MapId,bob.MapId);
        using var reloaded=new AllianceStore(root);Assert.Equal(alice.MapId,reloaded.Snapshot("alice").MapId);
        using var world=new AllianceStore(Path.Combine(root,"other-world"));Assert.NotEqual(alice.MapId,world.Snapshot("alice").MapId);
    }
    [Fact]
    public void JoiningAndRemovingAreSymmetricAndSnapshotsAreIsolated()
    {
        using var store=new AllianceStore(root);var bob=store.Snapshot("bob");
        store.Join("alice","  "+bob.MapId.ToUpperInvariant()+"  ");
        Assert.True(store.IsAllied("alice","bob"));Assert.True(store.IsAllied("bob","alice"));
        var alice=store.Snapshot("alice");alice.Allies[0]="mallory";Assert.True(store.IsAllied("alice","bob"));
        using(var reload=new AllianceStore(root))Assert.True(reload.IsAllied("bob","alice"));
        Assert.Throws<InvalidOperationException>(()=>store.Join("alice",bob.MapId));
        Assert.Throws<ArgumentException>(()=>store.Join("bob",bob.MapId));
        Assert.Throws<ArgumentException>(()=>store.Join("alice","bob"));
        Assert.Throws<KeyNotFoundException>(()=>store.Join("alice",new string('0',32)));
        Assert.False(store.Remove("mallory","bob"));Assert.True(store.IsAllied("bob","alice"));
        Assert.True(store.Remove("alice","bob"));Assert.False(store.IsAllied("bob","alice"));
        using var removed=new AllianceStore(root);Assert.Empty(removed.Snapshot("bob").Allies);
        Assert.Equal(bob.MapId,removed.Snapshot("bob").MapId);
    }
    [Fact]
    public void MigrationKeepsAcceptedAlliancesButDoesNotAcceptOldInvitations()
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root,"alliances.json"),"""{"alice":{"Allies":["bob"],"Incoming":["carol"],"Outgoing":[]},"bob":{"Allies":["alice"],"Incoming":[],"Outgoing":[]},"carol":{"Allies":[],"Outgoing":["alice"],"Incoming":[]}}""");
        using var store=new AllianceStore(root);Assert.Equal(new[]{"bob"},store.Snapshot("alice").Allies);
        Assert.False(store.IsAllied("alice","carol"));
        using var reloaded=new AllianceStore(root);Assert.True(reloaded.IsAllied("bob","alice"));
    }
    [Fact]
    public void FailedPersistenceNeverEnablesSharingInMemory()
    {
        using var store=new AllianceStore(root);var id=store.Snapshot("bob").MapId;
        var path=Path.Combine(root,"alliances.json");File.Delete(path);Directory.CreateDirectory(path);
        Assert.True(Record.Exception(()=>store.Join("alice",id)) is IOException or UnauthorizedAccessException);
        Assert.False(store.IsAllied("bob","alice"));Assert.False(store.IsAllied("alice","bob"));
    }
    [Fact]
    public void AvatarKeysPersistAndAlliancesAreBounded()
    {
        using var store=new AllianceStore(root);var key=new string('a',64);store.RememberAvatar("alice",key);
        using(var reloaded=new AllianceStore(root))Assert.Equal(key,reloaded.Avatar("alice"));
        store.RememberAvatar("alice","../invalid");Assert.Equal(key,store.Avatar("alice"));
        for(var i=0;i<AllianceStore.MaxAllies;i++)store.Join("alice",store.Snapshot("player-"+i).MapId);
        Assert.Throws<InvalidOperationException>(()=>store.Join("alice",store.Snapshot("overflow").MapId));
        Assert.Equal(AllianceStore.MaxAllies,store.Snapshot("alice").Allies.Length);
    }
    public void Dispose(){if(Directory.Exists(root))Directory.Delete(root,true);}
}
