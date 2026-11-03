#if !USE_PUN
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Xml;

internal static class UpnpIgdPortMapper
{
  const int SsdpPort = 1900;
  const int DiscoveryTimeoutMilliseconds = 2500;
  const int SocketReceiveTimeoutMilliseconds = 200;
  const int HttpTimeoutMilliseconds = 2500;
  const int MaxXmlCharacters = 65536;
  const int MaxSoapCharacters = 8192;
  const uint RouterLeaseSeconds = 3600;
  static readonly ushort[] ExternalPorts = { 7777, 17777, 27777 };

  internal sealed class Mapping
  {
    public readonly Uri ControlUrl;
    public readonly string ServiceType;
    public readonly string InternalAddress;
    public readonly ushort InternalPort;
    public readonly ushort ExternalPort;
    public readonly uint LeaseSeconds;

    public Mapping(Uri controlUrl, string serviceType, string internalAddress,
      ushort internalPort, ushort externalPort, uint leaseSeconds)
    {
      ControlUrl = controlUrl;
      ServiceType = serviceType;
      InternalAddress = internalAddress;
      InternalPort = internalPort;
      ExternalPort = externalPort;
      LeaseSeconds = leaseSeconds;
    }
  }

  internal sealed class MappingResult
  {
    public readonly Mapping Mapping;
    public readonly string Message;

    public MappingResult(Mapping mapping, string message)
    {
      Mapping = mapping;
      Message = message;
    }
  }

  sealed class GatewayService
  {
    public Uri ControlUrl;
    public string ServiceType;
    public IPAddress GatewayAddress;
  }

  sealed class SoapResult
  {
    public bool Succeeded;
    public string FaultCode;
    public string Error;
  }

  public static MappingResult TryCreate(ushort internalPort)
  {
    try
    {
      List<GatewayService> services = DiscoverGatewayServices();
      if (services.Count == 0)
        return new MappingResult(null, "No UPnP Internet gateway was found.");

      string lastError = "The UPnP gateway did not offer a usable WAN port-mapping service.";
      foreach (GatewayService service in services)
      {
        IPAddress internalAddress;
        if (!TryGetLocalAddress(service.GatewayAddress, out internalAddress))
        {
          lastError = "Could not determine the local IPv4 address for the UPnP gateway.";
          continue;
        }

        foreach (ushort externalPort in ExternalPorts)
        {
          SoapResult result = AddMapping(service, internalAddress, internalPort, externalPort, RouterLeaseSeconds);
          uint leaseSeconds = RouterLeaseSeconds;
          if (!result.Succeeded && result.FaultCode == "725")
          {
            result = AddMapping(service, internalAddress, internalPort, externalPort, 0);
            leaseSeconds = 0;
          }
          if (result.Succeeded)
          {
            var mapping = new Mapping(service.ControlUrl, service.ServiceType,
              internalAddress.ToString(), internalPort, externalPort, leaseSeconds);
            return new MappingResult(mapping,
              "UPnP mapped UDP " + internalPort + " to external port " + externalPort + ".");
          }

          lastError = result.Error;
          if (result.FaultCode == "718") continue;
          break;
        }
      }
      return new MappingResult(null, "Automatic UPnP mapping failed: " + lastError);
    }
    catch (Exception e)
    {
      return new MappingResult(null, "Automatic UPnP mapping failed: " + e.Message);
    }
  }

  public static string Renew(Mapping mapping)
  {
    if (mapping == null || mapping.LeaseSeconds == 0) return null;
    SoapResult result = AddMapping(mapping, mapping.LeaseSeconds);
    return result.Succeeded ? null : result.Error;
  }

  public static string Delete(Mapping mapping)
  {
    if (mapping == null) return null;
    string body = BuildEnvelope(mapping.ServiceType, "DeletePortMapping",
      new KeyValuePair<string, string>("NewRemoteHost", string.Empty),
      new KeyValuePair<string, string>("NewExternalPort", mapping.ExternalPort.ToString()),
      new KeyValuePair<string, string>("NewProtocol", "UDP"));
    SoapResult result = SendSoap(mapping.ControlUrl, mapping.ServiceType, "DeletePortMapping", body);
    if (result.Succeeded || result.FaultCode == "714") return null;
    return result.Error;
  }

  static bool TryGetLocalAddress(IPAddress gateway, out IPAddress address)
  {
    address = null;
    try
    {
      using (var routeSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
      {
        routeSocket.Connect(new IPEndPoint(gateway, SsdpPort));
        var local = ((IPEndPoint)routeSocket.LocalEndPoint).Address;
        if (local.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(local)) return false;
        address = local;
        return true;
      }
    }
    catch (SocketException)
    {
      return false;
    }
  }

  static List<GatewayService> DiscoverGatewayServices()
  {
    var locations = new Dictionary<string, IPAddress>(StringComparer.OrdinalIgnoreCase);
    string requestText = "M-SEARCH * HTTP/1.1\r\n"
      + "HOST: 239.255.255.250:1900\r\n"
      + "MAN: \"ssdp:discover\"\r\n"
      + "MX: 2\r\n"
      + "ST: urn:schemas-upnp-org:device:InternetGatewayDevice:1\r\n\r\n";
    byte[] request = Encoding.ASCII.GetBytes(requestText);
    DateTime deadline = DateTime.UtcNow.AddMilliseconds(DiscoveryTimeoutMilliseconds);

    using (var socket = new UdpClient(AddressFamily.InterNetwork))
    {
      socket.Client.ReceiveTimeout = SocketReceiveTimeoutMilliseconds;
      IPEndPoint multicast = new IPEndPoint(IPAddress.Parse("239.255.255.250"), SsdpPort);
      socket.Send(request, request.Length, multicast);
      while (DateTime.UtcNow < deadline)
      {
        IPEndPoint source = new IPEndPoint(IPAddress.Any, 0);
        try
        {
          byte[] response = socket.Receive(ref source);
          if (source.Address.AddressFamily != AddressFamily.InterNetwork
            || !IsPrivateGatewayAddress(source.Address)) continue;
          string location = FindHeader(Encoding.ASCII.GetString(response), "location");
          Uri parsed;
          if (string.IsNullOrEmpty(location)
            || !Uri.TryCreate(location, UriKind.Absolute, out parsed)
            || parsed.Scheme != Uri.UriSchemeHttp)
            continue;

          // UPnP descriptions are served by the responding gateway. Ignore a
          // mismatched LOCATION host instead of turning SSDP into arbitrary HTTP access.
          var safeLocation = new UriBuilder(parsed) { Host = source.Address.ToString() }.Uri;
          locations[safeLocation.AbsoluteUri] = source.Address;
        }
        catch (SocketException e) when (e.SocketErrorCode == SocketError.TimedOut
          || e.SocketErrorCode == SocketError.WouldBlock)
        {
        }
      }
    }

    var services = new List<GatewayService>();
    foreach (KeyValuePair<string, IPAddress> location in locations)
    {
      try
      {
        services.AddRange(ReadGatewayServices(new Uri(location.Key), location.Value));
      }
      catch (Exception)
      {
        // A gateway can advertise stale or unsupported device descriptions.
      }
    }
    services.Sort((left, right) =>
      StringComparer.Ordinal.Compare(right.ServiceType, left.ServiceType));
    return services;
  }

  static bool IsPrivateGatewayAddress(IPAddress address)
  {
    byte[] bytes = address.GetAddressBytes();
    return bytes.Length == 4
      && (bytes[0] == 10
        || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
        || (bytes[0] == 192 && bytes[1] == 168)
        || (bytes[0] == 169 && bytes[1] == 254)
        || (bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127));
  }

  static List<GatewayService> ReadGatewayServices(Uri location, IPAddress gateway)
  {
    string description = GetHttpText(location, MaxXmlCharacters);
    XmlDocument document = LoadXml(description, MaxXmlCharacters);
    XmlNode baseNode = document.SelectSingleNode("//*[local-name()='URLBase']");
    Uri baseUri = baseNode == null || string.IsNullOrWhiteSpace(baseNode.InnerText)
      ? location
      : new Uri(location, baseNode.InnerText.Trim());
    XmlNodeList serviceNodes = document.SelectNodes("//*[local-name()='service']");
    var result = new List<GatewayService>();
    if (serviceNodes == null) return result;

    foreach (XmlNode serviceNode in serviceNodes)
    {
      XmlNode typeNode = serviceNode.SelectSingleNode("*[local-name()='serviceType']");
      XmlNode controlNode = serviceNode.SelectSingleNode("*[local-name()='controlURL']");
      if (typeNode == null || controlNode == null) continue;
      string serviceType = typeNode.InnerText.Trim();
      if (!serviceType.StartsWith("urn:schemas-upnp-org:service:WANIPConnection:", StringComparison.OrdinalIgnoreCase)
        && !serviceType.StartsWith("urn:schemas-upnp-org:service:WANPPPConnection:", StringComparison.OrdinalIgnoreCase))
        continue;
      Uri controlUrl;
      if (!Uri.TryCreate(baseUri, controlNode.InnerText.Trim(), out controlUrl)
        || controlUrl.Scheme != Uri.UriSchemeHttp)
        continue;
      controlUrl = new UriBuilder(controlUrl)
      {
        Host = gateway.ToString(),
        UserName = string.Empty,
        Password = string.Empty
      }.Uri;
      result.Add(new GatewayService
      {
        ControlUrl = controlUrl,
        ServiceType = serviceType,
        GatewayAddress = gateway
      });
    }
    return result;
  }

  static string FindHeader(string response, string headerName)
  {
    using (var reader = new StringReader(response))
    {
      string line;
      while ((line = reader.ReadLine()) != null)
      {
        int separator = line.IndexOf(':');
        if (separator <= 0 || !string.Equals(line.Substring(0, separator).Trim(), headerName,
          StringComparison.OrdinalIgnoreCase)) continue;
        return line.Substring(separator + 1).Trim();
      }
    }
    return null;
  }

  static string GetHttpText(Uri url, int maxCharacters)
  {
    var request = (HttpWebRequest)WebRequest.Create(url);
    request.Method = "GET";
    request.Proxy = null;
    request.AllowAutoRedirect = false;
    request.Timeout = HttpTimeoutMilliseconds;
    request.ReadWriteTimeout = HttpTimeoutMilliseconds;
    using (var response = (HttpWebResponse)request.GetResponse())
    {
      if ((int)response.StatusCode < 200 || (int)response.StatusCode >= 300)
        throw new WebException("UPnP device description returned HTTP " + (int)response.StatusCode + ".");
      return ReadLimited(response.GetResponseStream(), maxCharacters);
    }
  }

  static XmlDocument LoadXml(string text, int maxCharacters)
  {
    var settings = new XmlReaderSettings
    {
      DtdProcessing = DtdProcessing.Prohibit,
      XmlResolver = null,
      MaxCharactersInDocument = maxCharacters
    };
    var document = new XmlDocument { XmlResolver = null };
    using (var reader = XmlReader.Create(new StringReader(text), settings))
      document.Load(reader);
    return document;
  }

  static string ReadLimited(Stream stream, int maxCharacters)
  {
    if (stream == null) return string.Empty;
    using (stream)
    using (var reader = new StreamReader(stream, Encoding.UTF8, true, 1024, false))
    {
      var result = new StringBuilder();
      char[] buffer = new char[2048];
      int count;
      while ((count = reader.Read(buffer, 0, buffer.Length)) > 0)
      {
        if (result.Length + count > maxCharacters)
          throw new InvalidDataException("UPnP response exceeded its size limit.");
        result.Append(buffer, 0, count);
      }
      return result.ToString();
    }
  }

  static SoapResult AddMapping(GatewayService service, IPAddress internalAddress,
    ushort internalPort, ushort externalPort, uint leaseSeconds)
  {
    var mapping = new Mapping(service.ControlUrl, service.ServiceType,
      internalAddress.ToString(), internalPort, externalPort, leaseSeconds);
    return AddMapping(mapping, leaseSeconds);
  }

  static SoapResult AddMapping(Mapping mapping, uint leaseSeconds)
  {
    string body = BuildEnvelope(mapping.ServiceType, "AddPortMapping",
      new KeyValuePair<string, string>("NewRemoteHost", string.Empty),
      new KeyValuePair<string, string>("NewExternalPort", mapping.ExternalPort.ToString()),
      new KeyValuePair<string, string>("NewProtocol", "UDP"),
      new KeyValuePair<string, string>("NewInternalPort", mapping.InternalPort.ToString()),
      new KeyValuePair<string, string>("NewInternalClient", mapping.InternalAddress),
      new KeyValuePair<string, string>("NewEnabled", "1"),
      new KeyValuePair<string, string>("NewPortMappingDescription", "Game Builder direct host"),
      new KeyValuePair<string, string>("NewLeaseDuration", leaseSeconds.ToString()));
    return SendSoap(mapping.ControlUrl, mapping.ServiceType, "AddPortMapping", body);
  }

  static SoapResult SendSoap(Uri controlUrl, string serviceType, string action, string body)
  {
    byte[] payload = Encoding.UTF8.GetBytes(body);
    var request = (HttpWebRequest)WebRequest.Create(controlUrl);
    request.Method = "POST";
    request.Proxy = null;
    request.AllowAutoRedirect = false;
    request.Timeout = HttpTimeoutMilliseconds;
    request.ReadWriteTimeout = HttpTimeoutMilliseconds;
    request.ContentType = "text/xml; charset=\"utf-8\"";
    request.Headers["SOAPAction"] = "\"" + serviceType + "#" + action + "\"";
    request.ContentLength = payload.Length;
    try
    {
      using (Stream stream = request.GetRequestStream())
        stream.Write(payload, 0, payload.Length);
      using (var response = (HttpWebResponse)request.GetResponse())
      {
        string responseBody = ReadLimited(response.GetResponseStream(), MaxSoapCharacters);
        return new SoapResult
        {
          Succeeded = (int)response.StatusCode >= 200 && (int)response.StatusCode < 300,
          Error = "UPnP " + action + " returned HTTP " + (int)response.StatusCode + ".",
          FaultCode = GetFaultCode(responseBody)
        };
      }
    }
    catch (WebException e)
    {
      string responseBody = string.Empty;
      int? statusCode = null;
      using (var response = e.Response as HttpWebResponse)
      {
        if (response != null)
        {
          statusCode = (int)response.StatusCode;
          try { responseBody = ReadLimited(response.GetResponseStream(), MaxSoapCharacters); }
          catch (Exception) { }
        }
      }
      string faultCode = GetFaultCode(responseBody);
      string error = "UPnP " + action + " failed"
        + (statusCode.HasValue ? " with HTTP " + statusCode.Value : string.Empty)
        + (string.IsNullOrEmpty(faultCode) ? ": " + e.Message : " (error " + faultCode + ")") + ".";
      return new SoapResult { Succeeded = false, FaultCode = faultCode, Error = error };
    }
  }

  static string GetFaultCode(string responseBody)
  {
    if (string.IsNullOrEmpty(responseBody)) return null;
    try
    {
      XmlDocument document = LoadXml(responseBody, MaxSoapCharacters);
      XmlNode code = document.SelectSingleNode("//*[local-name()='errorCode']");
      return code == null ? null : code.InnerText.Trim();
    }
    catch (Exception)
    {
      return null;
    }
  }

  static string BuildEnvelope(string serviceType, string action, params KeyValuePair<string, string>[] values)
  {
    var body = new StringBuilder();
    body.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>")
      .Append("<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" ")
      .Append("s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">")
      .Append("<s:Body><u:").Append(action).Append(" xmlns:u=\"")
      .Append(System.Security.SecurityElement.Escape(serviceType)).Append("\">");
    foreach (KeyValuePair<string, string> value in values)
    {
      body.Append('<').Append(value.Key).Append('>')
        .Append(System.Security.SecurityElement.Escape(value.Value ?? string.Empty))
        .Append("</").Append(value.Key).Append('>');
    }
    body.Append("</u:").Append(action).Append("></s:Body></s:Envelope>");
    return body.ToString();
  }
}
#endif
