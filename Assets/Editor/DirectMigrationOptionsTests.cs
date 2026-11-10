#if UNITY_EDITOR && !USE_PUN
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

public class DirectMigrationOptionsTests
{
  static readonly MethodInfo CreateOptionsMethod = typeof(NetworkingController).GetMethod(
    "CreateDirectMigrationOptions", BindingFlags.Instance | BindingFlags.NonPublic);

  GameObject controllerObject;
  GameBuilderApplication.GameOptions previousOptions;

  [SetUp]
  public void SetUp()
  {
    previousOptions = GameBuilderApplication.CurrentGameOptions;
    GameBuilderApplication.Get().SetGameOptionsDoNotUseExceptFromSceneController(
      new GameBuilderApplication.GameOptions
      {
        displayName = "Original game",
        directHostPassword = "password123",
        playOptions = new GameBuilderApplication.PlayOptions { isMultiplayer = true }
      });
    controllerObject = new GameObject("DirectMigrationOptionsTests");
    controllerObject.SetActive(false);
    controllerObject.AddComponent<NetworkingController>();
  }

  [TearDown]
  public void TearDown()
  {
    GameBuilderApplication.Get().SetGameOptionsDoNotUseExceptFromSceneController(previousOptions);
    if (controllerObject != null) Object.DestroyImmediate(controllerObject);
  }

  [Test]
  public void MigratedClientUsesDirectoryEndpointInsteadOfHosting()
  {
    Assert.IsNotNull(CreateOptionsMethod);
    var ticket = new DirectSessionDirectory.MigrationTicket
    {
      SessionId = "session-id",
      Token = "migration-token",
      Name = "Migrated game",
      Generation = 1,
      IsPublic = true
    };
    var migrated = new DirectSessionDirectory.SessionInfo
    {
      SessionId = ticket.SessionId,
      Name = "Migrated game",
      Address = "203.0.113.42",
      Port = 24567,
      AddressFamily = 4,
      ServerCertificatePem = "certificate",
      Generation = 2
    };

    var options = (GameBuilderApplication.GameOptions)CreateOptionsMethod.Invoke(
      controllerObject.GetComponent<NetworkingController>(), new object[] { ticket, migrated, false });

    Assert.AreEqual(migrated.Address, options.joinAddress);
    Assert.AreEqual((ushort)migrated.Port, options.joinPort);
    Assert.AreEqual(migrated.ServerCertificatePem, options.trustedHostCertificatePem);
    Assert.IsTrue(options.playOptions.isMultiplayer);
  }
}
#endif
