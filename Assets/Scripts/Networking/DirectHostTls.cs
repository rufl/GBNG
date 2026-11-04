using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

public static class DirectHostTls
{
  public const string ServerCommonName = "gamebuilder-direct-host";
  public const string InvitePrefix = "GBDIRECT1";
  public const string RelayInvitePrefix = "GBDIRECT2";
  const string CertificateBegin = "-----BEGIN CERTIFICATE-----";
  const string CertificateEnd = "-----END CERTIFICATE-----";
  const int MaximumCertificateBytes = 4096;
  const int MaximumPemCharacters = 8192;
  static HostCredentials hostCredentials;

  public sealed class HostCredentials
  {
    public readonly string CertificatePem;
    public readonly string PrivateKeyPem;

    internal HostCredentials(string certificatePem, string privateKeyPem)
    {
      CertificatePem = certificatePem;
      PrivateKeyPem = privateKeyPem;
    }
  }

  public static HostCredentials GetHostCredentials()
  {
    if (hostCredentials == null) hostCredentials = CreateHostCredentials();
    return hostCredentials;
  }

  public static HostCredentials CreateHostCredentials()
  {
    using (RSA rsa = new RSACryptoServiceProvider(2048))
    {
      ((RSACryptoServiceProvider)rsa).PersistKeyInCsp = false;
      if (rsa.KeySize != 2048) throw new CryptographicException("The platform could not create a 2048-bit direct-host key.");
      RSAParameters parameters = rsa.ExportParameters(true);
      byte[] algorithm = DerSequence(DerOid(new byte[] { 0x2a, 0x86, 0x48, 0x86, 0xf7, 0x0d, 0x01, 0x01, 0x0b }), DerNull());
      byte[] subject = CreateSubjectName();
      DateTime now = DateTime.UtcNow;
      byte[] validity = DerSequence(
        DerUtcTime(now.AddMinutes(-5)),
        DerUtcTime(now.AddYears(10)));
      byte[] publicKey = DerSequence(
        DerSequence(DerOid(new byte[] { 0x2a, 0x86, 0x48, 0x86, 0xf7, 0x0d, 0x01, 0x01, 0x01 }), DerNull()),
        DerBitString(DerSequence(DerInteger(parameters.Modulus), DerInteger(parameters.Exponent))));
      byte[] constraints = DerSequence(DerBoolean(true));
      byte[] serverAuthentication = DerSequence(DerOid(new byte[] { 0x2b, 0x06, 0x01, 0x05, 0x05, 0x07, 0x03, 0x01 }));
      byte[] extensions = DerSequence(
        DerSequence(DerOid(new byte[] { 0x55, 0x1d, 0x13 }), DerBoolean(true), DerOctetString(constraints)),
        DerSequence(DerOid(new byte[] { 0x55, 0x1d, 0x0f }), DerBoolean(true), DerOctetString(DerValue(0x03, new byte[] { 0x02, 0xa4 }))),
        DerSequence(DerOid(new byte[] { 0x55, 0x1d, 0x25 }), DerOctetString(serverAuthentication)));

      byte[] serial = new byte[16];
      using (RandomNumberGenerator random = RandomNumberGenerator.Create())
        random.GetBytes(serial);
      serial[0] = (byte)((serial[0] & 0x7f) | 0x01);

      byte[] tbsCertificate = DerSequence(
        DerValue(0xa0, DerInteger(new byte[] { 0x02 })),
        DerInteger(serial),
        algorithm,
        subject,
        validity,
        subject,
        publicKey,
        DerValue(0xa3, extensions));
      byte[] signature = rsa.SignData(tbsCertificate, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
      byte[] certificateDer = DerSequence(tbsCertificate, algorithm, DerBitString(signature));
      if (certificateDer.Length > MaximumCertificateBytes)
        throw new CryptographicException("Generated direct-host certificate exceeds the supported size.");

      byte[] privateKey = DerSequence(
        DerInteger(new byte[] { 0x00 }),
        DerInteger(parameters.Modulus),
        DerInteger(parameters.Exponent),
        DerInteger(parameters.D),
        DerInteger(parameters.P),
        DerInteger(parameters.Q),
        DerInteger(parameters.DP),
        DerInteger(parameters.DQ),
        DerInteger(parameters.InverseQ));
      return new HostCredentials(ToCertificatePem(certificateDer), ToPem("RSA PRIVATE KEY", privateKey));
    }
  }

  public static bool IsUsablePinnedCertificate(string certificatePem)
  {
    if (string.IsNullOrWhiteSpace(certificatePem) || certificatePem.Length > MaximumPemCharacters) return false;
    if (!TryReadCertificateDer(certificatePem, out byte[] der)) return false;
    return IsUsablePinnedCertificate(der);
  }

  public static string EncodeCertificateForInvite(string certificatePem)
  {
    if (!TryReadCertificateDer(certificatePem, out byte[] der) || !IsUsablePinnedCertificate(der))
      throw new ArgumentException("A valid Game Builder direct-host certificate is required.", nameof(certificatePem));
    return Convert.ToBase64String(der).TrimEnd('=').Replace('+', '-').Replace('/', '_');
  }
  public static string CreateInvite(string address, ushort port, string certificatePem)
  {
    string host = string.IsNullOrWhiteSpace(address) ? null : address.Trim();
    if (host == null || host.IndexOf('|') >= 0 || port == 0)
      throw new ArgumentException("A direct-host invite requires a hostname or IP address and a non-zero port.");
    return InvitePrefix + "|" + host + "|" + port.ToString(CultureInfo.InvariantCulture)
      + "|" + EncodeCertificateForInvite(certificatePem);
  }
  public static string CreateRelayInvite(
    string address,
    ushort port,
    string certificatePem,
    string relayAddress,
    ushort relayPort,
    string relaySessionId)
  {
    string host = string.IsNullOrWhiteSpace(address) ? null : address.Trim();
    string relay = string.IsNullOrWhiteSpace(relayAddress) ? null : relayAddress.Trim();
    if (host == null || host.IndexOf('|') >= 0 || port == 0
      || relay == null || relay.Length > 253 || relay.IndexOf('|') >= 0 || relayPort == 0
      || string.IsNullOrEmpty(relaySessionId) || relaySessionId.Length != 24)
      throw new ArgumentException("A relayed direct-host invite requires valid host and relay endpoints.");
    for (int i = 0; i < relay.Length; i++)
      if (char.IsWhiteSpace(relay[i]) || char.IsControl(relay[i]))
        throw new ArgumentException("The relay address must be a hostname or IP.", nameof(relayAddress));
    for (int i = 0; i < relaySessionId.Length; i++)
    {
      char c = relaySessionId[i];
      if (!(c >= 'A' && c <= 'Z') && !(c >= 'a' && c <= 'z')
        && !(c >= '0' && c <= '9') && c != '-' && c != '_')
        throw new ArgumentException("The relay session id is invalid.", nameof(relaySessionId));
    }
    return RelayInvitePrefix + "|" + host + "|" + port.ToString(CultureInfo.InvariantCulture)
      + "|" + relay + "|" + relayPort.ToString(CultureInfo.InvariantCulture)
      + "|" + relaySessionId + "|" + EncodeCertificateForInvite(certificatePem);
  }

  public static bool TryDecodeCertificateFromInvite(string encodedCertificate, out string certificatePem)
  {
    certificatePem = null;
    if (string.IsNullOrEmpty(encodedCertificate) || encodedCertificate.Length > 5500) return false;
    for (int i = 0; i < encodedCertificate.Length; i++)
    {
      char c = encodedCertificate[i];
      if (!(c >= 'A' && c <= 'Z') && !(c >= 'a' && c <= 'z')
        && !(c >= '0' && c <= '9') && c != '-' && c != '_') return false;
    }
    if (encodedCertificate.Length % 4 == 1) return false;

    string base64 = encodedCertificate.Replace('-', '+').Replace('_', '/');
    base64 += new string('=', (4 - base64.Length % 4) % 4);
    try
    {
      byte[] der = Convert.FromBase64String(base64);
      if (der.Length > MaximumCertificateBytes || !IsUsablePinnedCertificate(der)) return false;
      certificatePem = ToCertificatePem(der);
      return true;
    }
    catch (FormatException)
    {
      return false;
    }
  }

  static bool TryReadCertificateDer(string certificatePem, out byte[] der)
  {
    der = null;
    string pem = certificatePem.Trim();
    if (!pem.StartsWith(CertificateBegin, StringComparison.Ordinal)) return false;
    int end = pem.IndexOf(CertificateEnd, CertificateBegin.Length, StringComparison.Ordinal);
    if (end < 0 || !string.IsNullOrWhiteSpace(pem.Substring(end + CertificateEnd.Length))) return false;
    string body = pem.Substring(CertificateBegin.Length, end - CertificateBegin.Length);
    try
    {
      der = Convert.FromBase64String(body);
      if (der.Length == 0 || der.Length > MaximumCertificateBytes)
      {
        der = null;
        return false;
      }
      return true;
    }
    catch (FormatException)
    {
      return false;
    }
  }

  static bool IsUsablePinnedCertificate(byte[] der)
  {
    try
    {
      using (X509Certificate2 certificate = new X509Certificate2(der))
      {
        DateTime now = DateTime.UtcNow;
        if (certificate.NotBefore.ToUniversalTime() > now || certificate.NotAfter.ToUniversalTime() <= now)
          return false;
        string expectedSubject = "CN=" + ServerCommonName;
        if (certificate.Subject != expectedSubject || certificate.Issuer != expectedSubject)
          return false;

        string caConstraints = Convert.ToBase64String(DerSequence(DerBoolean(true)));
        string serverAuthentication = Convert.ToBase64String(DerSequence(DerOid(new byte[] { 0x2b, 0x06, 0x01, 0x05, 0x05, 0x07, 0x03, 0x01 })));
        bool isCertificateAuthority = false;
        bool permitsServerAuthentication = false;
        foreach (X509Extension extension in certificate.Extensions)
        {
          if (extension.Oid.Value == "2.5.29.19" && Convert.ToBase64String(extension.RawData) == caConstraints)
            isCertificateAuthority = true;
          if (extension.Oid.Value == "2.5.29.37" && Convert.ToBase64String(extension.RawData) == serverAuthentication)
            permitsServerAuthentication = true;
        }
        return isCertificateAuthority && permitsServerAuthentication;
      }
    }
    catch (CryptographicException)
    {
      return false;
    }
    catch (ArgumentException)
    {
      return false;
    }
  }

  static byte[] CreateSubjectName()
  {
    byte[] commonNameOid = DerOid(new byte[] { 0x55, 0x04, 0x03 });
    byte[] commonName = DerValue(0x0c, Encoding.UTF8.GetBytes(ServerCommonName));
    return DerSequence(DerValue(0x31, DerSequence(commonNameOid, commonName)));
  }

  static byte[] DerInteger(byte[] value)
  {
    int first = 0;
    while (first < value.Length - 1 && value[first] == 0) first++;
    int length = value.Length - first;
    bool needsLeadingZero = (value[first] & 0x80) != 0;
    byte[] integer = new byte[length + (needsLeadingZero ? 1 : 0)];
    Buffer.BlockCopy(value, first, integer, needsLeadingZero ? 1 : 0, length);
    return DerValue(0x02, integer);
  }

  static byte[] DerUtcTime(DateTime value)
  {
    return DerValue(0x17, Encoding.ASCII.GetBytes(value.ToUniversalTime().ToString("yyMMddHHmmss'Z'", CultureInfo.InvariantCulture)));
  }

  static byte[] DerBoolean(bool value)
  {
    return DerValue(0x01, new byte[] { value ? (byte)0xff : (byte)0x00 });
  }

  static byte[] DerNull()
  {
    return DerValue(0x05, new byte[0]);
  }

  static byte[] DerOid(byte[] value)
  {
    return DerValue(0x06, value);
  }

  static byte[] DerOctetString(byte[] value)
  {
    return DerValue(0x04, value);
  }

  static byte[] DerBitString(byte[] value)
  {
    byte[] bits = new byte[value.Length + 1];
    Buffer.BlockCopy(value, 0, bits, 1, value.Length);
    return DerValue(0x03, bits);
  }

  static byte[] DerSequence(params byte[][] values)
  {
    return DerValue(0x30, Concatenate(values));
  }

  static byte[] DerValue(byte tag, byte[] value)
  {
    List<byte> result = new List<byte>(value.Length + 5);
    result.Add(tag);
    if (value.Length < 128)
    {
      result.Add((byte)value.Length);
    }
    else if (value.Length <= byte.MaxValue)
    {
      result.Add(0x81);
      result.Add((byte)value.Length);
    }
    else
    {
      result.Add(0x82);
      result.Add((byte)(value.Length >> 8));
      result.Add((byte)value.Length);
    }
    result.AddRange(value);
    return result.ToArray();
  }

  static byte[] Concatenate(byte[][] values)
  {
    int length = 0;
    for (int i = 0; i < values.Length; i++) length += values[i].Length;
    byte[] result = new byte[length];
    int offset = 0;
    for (int i = 0; i < values.Length; i++)
    {
      Buffer.BlockCopy(values[i], 0, result, offset, values[i].Length);
      offset += values[i].Length;
    }
    return result;
  }

  static string ToCertificatePem(byte[] der)
  {
    return ToPem("CERTIFICATE", der);
  }

  static string ToPem(string label, byte[] bytes)
  {
    string base64 = Convert.ToBase64String(bytes);
    StringBuilder pem = new StringBuilder(base64.Length + 80);
    pem.Append("-----BEGIN ").Append(label).Append("-----\n");
    for (int i = 0; i < base64.Length; i += 64)
      pem.Append(base64, i, Math.Min(64, base64.Length - i)).Append('\n');
    pem.Append("-----END ").Append(label).Append("-----\n");
    return pem.ToString();
  }
}
