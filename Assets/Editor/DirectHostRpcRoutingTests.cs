#if UNITY_EDITOR && !USE_PUN
using System.Reflection;
using NUnit.Framework;

public class DirectHostRpcRoutingTests
{
  static bool ShouldInvokeLocally(PhotonTargets target, int targetPlayerId, int localPlayerId, bool active, bool isServer)
  {
    MethodInfo policy = typeof(DirectHostRpcRouter).GetMethod(
      "ShouldInvokeRpcLocally", BindingFlags.Static | BindingFlags.NonPublic);
    Assert.IsNotNull(policy);
    return (bool)policy.Invoke(null, new object[] { target, targetPlayerId, localPlayerId, active, isServer });
  }

  static bool ShouldEchoToOrigin(PhotonTargets target, int targetPlayerId)
  {
    MethodInfo policy = typeof(DirectHostRpcRouter).GetMethod(
      "ShouldEchoAllViaServerToOrigin", BindingFlags.Static | BindingFlags.NonPublic);
    Assert.IsNotNull(policy);
    return (bool)policy.Invoke(null, new object[] { target, targetPlayerId });
  }

  [Test]
  public void AllViaServerWaitsForHostOnClientsAndEchoesToOrigin()
  {
    Assert.IsFalse(ShouldInvokeLocally(PhotonTargets.AllViaServer, 0, 2, true, false));
    Assert.IsTrue(ShouldInvokeLocally(PhotonTargets.AllViaServer, 0, 1, true, true));
    Assert.IsTrue(ShouldInvokeLocally(PhotonTargets.AllViaServer, 0, 2, false, false));
    Assert.IsTrue(ShouldEchoToOrigin(PhotonTargets.AllViaServer, 0));
    Assert.IsFalse(ShouldEchoToOrigin(PhotonTargets.AllViaServer, 2));
  }

  [Test]
  public void RpcTargetsRetainLocalOthersAndMasterClientSemantics()
  {
    Assert.IsTrue(ShouldInvokeLocally(PhotonTargets.All, 0, 2, true, false));
    Assert.IsFalse(ShouldInvokeLocally(PhotonTargets.Others, 0, 2, true, false));
    Assert.IsFalse(ShouldInvokeLocally(PhotonTargets.MasterClient, 0, 2, true, false));
    Assert.IsTrue(ShouldInvokeLocally(PhotonTargets.MasterClient, 0, 1, true, true));
    Assert.IsTrue(ShouldInvokeLocally(PhotonTargets.All, 2, 2, true, false));
    Assert.IsFalse(ShouldInvokeLocally(PhotonTargets.All, 3, 2, true, false));
    Assert.IsFalse(ShouldEchoToOrigin(PhotonTargets.All, 0));
  }
}
#endif
