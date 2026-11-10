#if UNITY_EDITOR && !USE_PUN
using System.Reflection;
using NUnit.Framework;

public class DirectPeerListTests
{
  static readonly MethodInfo ClearDirectSessionMethod = typeof(PhotonNetwork).GetMethod(
    "ClearDirectSession", BindingFlags.Static | BindingFlags.NonPublic);
  static readonly MethodInfo RegisterDirectPeerMethod = typeof(PhotonNetwork).GetMethod(
    "RegisterDirectPeer", BindingFlags.Static | BindingFlags.NonPublic);
  static readonly MethodInfo RemoveDirectPeerMethod = typeof(PhotonNetwork).GetMethod(
    "RemoveDirectPeer", BindingFlags.Static | BindingFlags.NonPublic);
  static readonly MethodInfo BuildMigrationReservationsMethod = typeof(NetworkingController).GetMethod(
    "BuildDirectMigrationReservedPlayerIds", BindingFlags.Static | BindingFlags.NonPublic);


  [SetUp]
  public void SetUp()
  {
    ClearDirectSessionMethod.Invoke(null, null);
  }

  [TearDown]
  public void TearDown()
  {
    ClearDirectSessionMethod.Invoke(null, null);
  }

  [Test]
  public void RemoteIdentityUpdatesMigrationCandidateRoster()
  {
    Assert.IsNotNull(RegisterDirectPeerMethod);
    Assert.IsNotNull(RemoveDirectPeerMethod);
    Assert.IsTrue((bool)RegisterDirectPeerMethod.Invoke(null, new object[] { 3, 17UL }));

    Assert.AreEqual(2, PhotonNetwork.playerList.Length);
    Assert.AreEqual(1, PhotonNetwork.playerList[0].ID);
    Assert.AreEqual(3, PhotonNetwork.playerList[1].ID);
    Assert.AreEqual(1, PhotonNetwork.otherPlayers.Length);
    Assert.AreEqual(3, PhotonNetwork.otherPlayers[0].ID);

    Assert.IsTrue((bool)RemoveDirectPeerMethod.Invoke(null, new object[] { 3 }));
    Assert.AreEqual(1, PhotonNetwork.playerList.Length);
    Assert.AreEqual(0, PhotonNetwork.otherPlayers.Length);
  }
  [Test]
  public void MigrationReservesKnownPeersWhenTheUiRosterIsStale()
  {
    Assert.IsNotNull(BuildMigrationReservationsMethod);
    PhotonNetwork.player = new PhotonPlayer { ID = 2, NickName = "Candidate" };
    PhotonNetwork.playerList = new[]
    {
      new PhotonPlayer { ID = 1, NickName = "Host" },
      PhotonNetwork.player
    };
    Assert.IsTrue((bool)RegisterDirectPeerMethod.Invoke(null, new object[] { 3, 17UL }));

    // Migration must use transport identities, not a UI roster that can lag an identity packet.
    PhotonNetwork.playerList = new[]
    {
      new PhotonPlayer { ID = 1, NickName = "Host" },
      PhotonNetwork.player
    };
    string reserved = (string)BuildMigrationReservationsMethod.Invoke(null, new object[] { 2 });

    Assert.AreEqual("3", reserved);
  }
}
#endif
