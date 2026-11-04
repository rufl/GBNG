#if UNITY_EDITOR && !USE_PUN
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
