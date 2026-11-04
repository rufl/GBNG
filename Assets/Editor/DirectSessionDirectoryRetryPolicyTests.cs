#if UNITY_EDITOR && !USE_PUN
using System.Reflection;
using NUnit.Framework;

public class DirectSessionDirectoryRetryPolicyTests
{
  [Test]
  public void PermanentMigrationRejectionStopsRetriesButTransientFailuresRetry()
  {
    MethodInfo policy = typeof(DirectSessionDirectory).GetMethod(
      "IsPermanentMigrationRejection", BindingFlags.Static | BindingFlags.NonPublic);
    Assert.IsNotNull(policy);
    Assert.IsTrue((bool)policy.Invoke(null, new object[] { 400L }));
    Assert.IsFalse((bool)policy.Invoke(null, new object[] { 409L }));
    Assert.IsFalse((bool)policy.Invoke(null, new object[] { 502L }));
    Assert.IsFalse((bool)policy.Invoke(null, new object[] { 0L }));
  }
}
#endif
