#if UNITY_EDITOR && !USE_PUN
using System;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using NUnit.Framework;

public class DirectHostRelayInviteTests
{
  const string RelaySessionId = "AAAAAAAAAAAAAAAAAAAAAAAA";
  static readonly DirectHostTls.HostCredentials Credentials = DirectHostTls.CreateHostCredentials();

  [Test]
  public void RelayInvitePreservesPinnedCertificateAndEndpoints()
  {
    string invite = DirectHostTls.CreateRelayInvite(
      "games.example.org", 17777, Credentials.CertificatePem,
      "relay.example.org", 47801, RelaySessionId);

    Assert.IsTrue(TryParse(invite, out object[] values));
    Assert.AreEqual("games.example.org", values[1]);
    Assert.AreEqual((ushort)17777, values[2]);
    Assert.AreEqual(Credentials.CertificatePem, values[3]);
    Assert.AreEqual("relay.example.org", values[4]);
    Assert.AreEqual((ushort)47801, values[5]);
    Assert.AreEqual(RelaySessionId, values[6]);
  }

  [Test]
  public void LegacyDirectInviteRemainsJoinableWithoutRelayFields()
  {
    string invite = DirectHostTls.CreateInvite(
      "games.example.org", 7777, Credentials.CertificatePem);

    Assert.IsTrue(TryParse(invite, out object[] values));
    Assert.IsNull(values[4]);
    Assert.AreEqual((ushort)0, values[5]);
    Assert.IsNull(values[6]);
  }

  [Test]
  public void RelayInviteRejectsMalformedSessionIdsAndControlPorts()
  {
    string valid = DirectHostTls.CreateRelayInvite(
      "games.example.org", 17777, Credentials.CertificatePem,
      "relay.example.org", 47801, RelaySessionId);
    string invalidSession = valid.Replace(RelaySessionId, "../AAAAAAAAAAAAAAAAAAAA");
    string invalidPort = valid.Replace("|47801|", "|0|");

    Assert.IsFalse(TryParse(invalidSession, out _));
    Assert.IsFalse(TryParse(invalidPort, out _));
  }


  [Test]
  public void RelayAddressSelectionSkipsUnreachableAddressFamily()
  {
    IPAddress ipv4 = IPAddress.Parse("192.0.2.10");
    IPAddress ipv6 = IPAddress.Parse("2001:db8::10");
    MethodInfo selector = typeof(DirectHostUdpRelay).GetMethod(
      "SelectRelayAddress", BindingFlags.Static | BindingFlags.NonPublic);
    Assert.IsNotNull(selector);

    Func<IPAddress, bool> canReach = address => address.AddressFamily == AddressFamily.InterNetworkV6;
    IPAddress selected = (IPAddress)selector.Invoke(null, new object[]
    {
      new[] { ipv4, ipv6 },
      canReach
    });

    Assert.AreEqual(ipv6, selected);
  }

  [Test]
  public void RelayAddressSelectionReportsWhenNoFamilyHasARoute()
  {
    MethodInfo selector = typeof(DirectHostUdpRelay).GetMethod(
      "SelectRelayAddress", BindingFlags.Static | BindingFlags.NonPublic);
    Assert.IsNotNull(selector);

    System.Reflection.TargetInvocationException error =
      Assert.Throws<System.Reflection.TargetInvocationException>(() =>
      {
        selector.Invoke(null, new object[]
        {
          new[] { IPAddress.Parse("192.0.2.10"), IPAddress.Parse("2001:db8::10") },
          new Func<IPAddress, bool>(address => false)
        });
      });

    Assert.IsInstanceOf<SocketException>(error.InnerException);
    Assert.AreEqual(SocketError.NetworkUnreachable, ((SocketException)error.InnerException).SocketErrorCode);
  }

  static bool TryParse(string invite, out object[] values)
  {
    values = new object[] { invite, null, (ushort)0, null, null, (ushort)0, null };
    MethodInfo parser = typeof(MultiplayerMenu).GetMethod(
      "TryParseDirectHostInvite", BindingFlags.Static | BindingFlags.NonPublic);
    Assert.IsNotNull(parser);
    return (bool)parser.Invoke(null, values);
  }
}
#endif
