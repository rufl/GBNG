#if UNITY_EDITOR && !USE_PUN
using System;
using System.Reflection;
using NUnit.Framework;
using Unity.Netcode;
using UnityEngine;

public class DirectHostPasswordTests
{
  static readonly Type PasswordType = typeof(DirectHostTls).Assembly.GetType("DirectHostPassword");
  static readonly MethodInfo TryEncodeMethod = PasswordType.GetMethod(
    "TryEncode", BindingFlags.Static | BindingFlags.NonPublic);
  static readonly MethodInfo MatchesMethod = PasswordType.GetMethod(
    "Matches", BindingFlags.Static | BindingFlags.NonPublic);
  static readonly FieldInfo PasswordBytesField = typeof(NetworkingController).GetField(
    "directHostPasswordBytes", BindingFlags.Instance | BindingFlags.NonPublic);
  static readonly MethodInfo ApproveConnectionMethod = typeof(NetworkingController).GetMethod(
    "ApproveDirectHostConnection", BindingFlags.Instance | BindingFlags.NonPublic);

  GameObject controllerObject;
  NetworkingController controller;

  [SetUp]
  public void SetUp()
  {
    controllerObject = new GameObject("DirectHostPasswordTests");
    controllerObject.SetActive(false);
    controller = controllerObject.AddComponent<NetworkingController>();
  }

  [TearDown]
  public void TearDown()
  {
    if (controllerObject != null)
      UnityEngine.Object.DestroyImmediate(controllerObject);
  }


  [TestCase("", true)]
  [TestCase("12345678", true)]
  [TestCase("1234567", false)]
  [TestCase("éééé", true)]
  [TestCase("ééééé", true)]
  public void PasswordEncodingEnforcesUtf8ByteBounds(string password, bool expectedValid)
  {
    object[] arguments = { password, null };

    bool valid = (bool)TryEncodeMethod.Invoke(null, arguments);

    Assert.AreEqual(expectedValid, valid);
    Assert.IsNotNull(arguments[1]);
  }

  [Test]
  public void PasswordEncodingAcceptsMaximumAndRejectsOversizeValues()
  {
    Assert.IsTrue(TryEncode(new string('x', 128), out byte[] maximum));
    Assert.AreEqual(128, maximum.Length);
    Assert.IsFalse(TryEncode(new string('x', 129), out byte[] oversized));
    Assert.AreEqual(0, oversized.Length);
  }

  [Test]
  public void PasswordEncodingEnforcesMaximumUtf8ByteCount()
  {
    Assert.IsTrue(TryEncode(new string('é', 64), out byte[] maximumMultibyte));
    Assert.AreEqual(128, maximumMultibyte.Length);
    Assert.IsFalse(TryEncode(new string('é', 65), out byte[] oversizedMultibyte));
    Assert.AreEqual(0, oversizedMultibyte.Length);
  }

  [Test]
  public void PasswordComparisonAcceptsOnlyTheExactSecret()
  {
    byte[] expected = { 1, 2, 3, 4, 5, 6, 7, 8 };

    Assert.IsTrue(Matches(expected, (byte[])expected.Clone()));
    Assert.IsFalse(Matches(expected, new byte[] { 1, 2, 3, 4, 5, 6, 7, 9 }));
    Assert.IsFalse(Matches(expected, new byte[] { 1, 2, 3, 4, 5, 6, 7 }));
    Assert.IsFalse(Matches(expected, null));
    Assert.IsFalse(Matches(expected, new byte[129]));

  }

  [Test]
  public void EmptyPasswordMatchesOnlyAnEmptyConnectionPayload()
  {
    Assert.IsTrue(Matches(Array.Empty<byte>(), Array.Empty<byte>()));
    Assert.IsTrue(Matches(Array.Empty<byte>(), null));
    Assert.IsFalse(Matches(Array.Empty<byte>(), new byte[] { 0 }));
  }

  [Test]
  public void ConnectionApprovalChecksRemotePasswordAndDisablesNgoPlayerSpawn()
  {
    Assert.IsTrue(TryEncode("shared-password", out byte[] expected));

    NetworkManager.ConnectionApprovalResponse accepted = Approve(1, expected, expected);
    Assert.IsTrue(accepted.Approved);
    Assert.IsFalse(accepted.CreatePlayerObject);
    Assert.AreEqual(string.Empty, accepted.Reason);

    NetworkManager.ConnectionApprovalResponse rejected = Approve(
      1, expected, EncodeForTest("wrong-password"));
    Assert.IsFalse(rejected.Approved);
    Assert.IsFalse(rejected.CreatePlayerObject);
    Assert.AreEqual("The shared host password is incorrect.", rejected.Reason);
  }

  [Test]
  public void ConnectionApprovalAllowsOpenHostsAndTheLocalHostClient()
  {
    NetworkManager.ConnectionApprovalResponse openHost = Approve(
      1, Array.Empty<byte>(), Array.Empty<byte>());
    Assert.IsTrue(openHost.Approved);

    NetworkManager.ConnectionApprovalResponse wrongPassword = Approve(
      1, EncodeForTest("shared-password"), Array.Empty<byte>());
    Assert.IsFalse(wrongPassword.Approved);

    NetworkManager.ConnectionApprovalResponse localHost = Approve(
      NetworkManager.ServerClientId, EncodeForTest("shared-password"), Array.Empty<byte>());
    Assert.IsTrue(localHost.Approved);
  }

  NetworkManager.ConnectionApprovalResponse Approve(ulong clientId, byte[] expected, byte[] provided)
  {
    PasswordBytesField.SetValue(controller, expected);
    var request = new NetworkManager.ConnectionApprovalRequest
    {
      ClientNetworkId = clientId,
      Payload = provided
    };
    var response = new NetworkManager.ConnectionApprovalResponse();
    ApproveConnectionMethod.Invoke(controller, new object[] { request, response });
    return response;
  }

  static byte[] EncodeForTest(string password)
  {
    Assert.IsTrue(TryEncode(password, out byte[] encoded));
    return encoded;
  }

  static bool TryEncode(string password, out byte[] encoded)
  {
    object[] arguments = { password, null };
    bool valid = (bool)TryEncodeMethod.Invoke(null, arguments);
    encoded = (byte[])arguments[1];
    return valid;
  }

  static bool Matches(byte[] expected, byte[] provided)
  {
    return (bool)MatchesMethod.Invoke(null, new object[] { expected, provided });
  }
}
#endif
