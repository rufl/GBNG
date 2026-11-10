#if UNITY_EDITOR && !USE_PUN
using System.Reflection;
using NUnit.Framework;

public class DirectPlayerLifecycleTests
{
  static readonly MethodInfo PublishDepartureMethod = typeof(NetworkingController).GetMethod(
    "ShouldPublishDirectPlayerDeparture", BindingFlags.Static | BindingFlags.NonPublic);

  static bool ShouldPublishDeparture(bool hadIdentity)
  {
    Assert.IsNotNull(PublishDepartureMethod);
    return (bool)PublishDepartureMethod.Invoke(null, new object[] { hadIdentity });
  }

  [Test]
  public void AssignmentOnlyDisconnectDoesNotPublishPlayerDeparture()
  {
    Assert.IsFalse(ShouldPublishDeparture(false));
    Assert.IsTrue(ShouldPublishDeparture(true));
  }
}
#endif
