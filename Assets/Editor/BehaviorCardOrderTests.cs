#if UNITY_EDITOR
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;

public class BehaviorCardOrderTests
{
  static readonly BindingFlags PrivateStatic = BindingFlags.NonPublic | BindingFlags.Static;

  [Test]
  public void SavedOrderWinsAndLegacyOrNewUsesAppendInBehaviorOrder()
  {
    List<string> merged = Merge(
      new[] { "second", "stale", "second", null, "" },
      new[] { "first", "second", "third", null });

    CollectionAssert.AreEqual(new[] { "second", "first", "third" }, merged);
  }

  [Test]
  public void MissingSavedOrderPreservesLegacyBehaviorUseOrder()
  {
    List<string> merged = Merge(null, new[] { "first", "second" });

    CollectionAssert.AreEqual(new[] { "first", "second" }, merged);
  }

  [Test]
  public void IndexedInsertAndAppendKeepTheRequestedDeckOrder()
  {
    List<string> order = new List<string> { "first", "second", "third" };
    InvokeInsert(order, "third", 1);
    CollectionAssert.AreEqual(new[] { "first", "third", "second" }, order);

    InvokeInsert(order, "fourth", -1);
    CollectionAssert.AreEqual(new[] { "first", "third", "second", "fourth" }, order);

    InvokeInsert(order, "second", 100);
    CollectionAssert.AreEqual(new[] { "first", "third", "fourth", "second" }, order);
  }

  static List<string> Merge(IEnumerable<string> savedUseIds, IEnumerable<string> assignedUseIds)
  {
    MethodInfo method = typeof(BehaviorCards).GetMethod("MergeCustomCardUseOrder", PrivateStatic);
    Assert.IsNotNull(method);
    return (List<string>)method.Invoke(null, new object[] { savedUseIds, assignedUseIds });
  }

  static void InvokeInsert(List<string> order, string useId, int index)
  {
    MethodInfo method = typeof(BehaviorCards).GetMethod("InsertCustomCardUseId", PrivateStatic);
    Assert.IsNotNull(method);
    method.Invoke(null, new object[] { order, useId, index });
  }
}
#endif
