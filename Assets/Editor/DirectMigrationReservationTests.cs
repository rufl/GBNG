#if UNITY_EDITOR && !USE_PUN
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

public class DirectMigrationReservationTests
{
  static readonly MethodInfo ClearDirectSessionMethod = typeof(PhotonNetwork).GetMethod(
    "ClearDirectSession", BindingFlags.Static | BindingFlags.NonPublic);
  static readonly MethodInfo SetReservedPlayerIdsMethod = typeof(PhotonNetwork).GetMethod(
    "SetDirectMigrationReservedPlayerIds", BindingFlags.Static | BindingFlags.NonPublic);
  static readonly MethodInfo ExpireReservationsMethod = typeof(PhotonNetwork).GetMethod(
    "ExpireDirectMigrationReservations", BindingFlags.Static | BindingFlags.NonPublic);
  static readonly FieldInfo ReservedPlayerIdsField = typeof(PhotonNetwork).GetField(
    "directMigrationReservedPlayerIds", BindingFlags.Static | BindingFlags.NonPublic);

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
  public void FormerPlayerSlotsRemainReservedThroughTheRecoveryWatch()
  {
    Assert.IsNotNull(SetReservedPlayerIdsMethod);
    Assert.IsNotNull(ExpireReservationsMethod);
    Assert.IsNotNull(ReservedPlayerIdsField);

    SetReservedPlayerIdsMethod.Invoke(null, new object[] { "2,3" });
    ExpireReservationsMethod.Invoke(null, new object[] { Time.realtimeSinceStartup + 241f });

    var reserved = (HashSet<int>)ReservedPlayerIdsField.GetValue(null);
    CollectionAssert.AreEquivalent(new[] { 2, 3 }, reserved);
  }
}
#endif
