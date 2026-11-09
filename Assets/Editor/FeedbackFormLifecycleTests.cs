#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

public class FeedbackFormLifecycleTests
{
  [UnityTest]
  public IEnumerator SubmissionCompletesAfterFormHostIsDestroyed()
  {
    using (LocalHttpServer server = new LocalHttpServer(200))
    {
      server.Start();
      GameObject host = new GameObject("Feedback form lifecycle test");
      bool completed = false;
      string error = null;
      try
      {
        host.AddComponent<FeedbackForm>();
        MethodInfo post = typeof(FeedbackForm).GetMethod("PostToForm", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.IsNotNull(post, "FeedbackForm must expose its internal submission path to this test.");
        post.Invoke(null, new object[] { server.Url, "test feedback", new Action<string>(value =>
        {
          error = value;
          completed = true;
        }) });

        for (int frame = 0; frame < 120 && !server.RequestReceived; frame++) yield return null;
        Assert.IsTrue(server.RequestReceived, "The local endpoint did not receive the feedback POST.");

        UnityEngine.Object.DestroyImmediate(host);
        host = null;
        server.AllowResponse();

        for (int frame = 0; frame < 120 && !completed; frame++) yield return null;
        Assert.IsTrue(completed, "Feedback completion must not depend on the form GameObject.");
        Assert.IsNull(error, "The local feedback endpoint should return HTTP 200.");

        for (int frame = 0; frame < 120 && !server.Finished; frame++) yield return null;
        Assert.IsTrue(server.Succeeded, "The local endpoint did not finish the request successfully.");
      }
      finally
      {
        server.AllowResponse();
        if (host != null) UnityEngine.Object.DestroyImmediate(host);
      }
    }
  }

  sealed class LocalHttpServer : IDisposable
  {
    readonly int statusCode;
    readonly ManualResetEvent requestReceived = new ManualResetEvent(false);
    readonly ManualResetEvent allowResponse = new ManualResetEvent(false);
    TcpListener listener;
    Task<bool> requestTask;

    public LocalHttpServer(int statusCode)
    {
      this.statusCode = statusCode;
    }

    public string Url { get; private set; }
    public bool RequestReceived { get { return requestReceived.WaitOne(0); } }
    public bool Finished { get { return requestTask != null && requestTask.IsCompleted; } }
    public bool Succeeded { get { return requestTask != null && requestTask.GetAwaiter().GetResult(); } }

    public void Start()
    {
      listener = new TcpListener(IPAddress.Loopback, 0);
      listener.Start();
      int port = ((IPEndPoint)listener.LocalEndpoint).Port;
      Url = "http://127.0.0.1:" + port + "/feedback";
      requestTask = Task.Run(ServeOneRequest);
    }

    public void AllowResponse()
    {
      allowResponse.Set();
    }

    bool ServeOneRequest()
    {
      try
      {
        using (TcpClient client = listener.AcceptTcpClient())
        using (NetworkStream stream = client.GetStream())
        {
          client.ReceiveTimeout = 10000;
          List<byte> headerBytes = new List<byte>();
          byte[] singleByte = new byte[1];
          while (headerBytes.Count < 16384)
          {
            if (stream.Read(singleByte, 0, 1) != 1) return false;
            headerBytes.Add(singleByte[0]);
            int count = headerBytes.Count;
            if (count >= 4
              && headerBytes[count - 4] == '\r' && headerBytes[count - 3] == '\n'
              && headerBytes[count - 2] == '\r' && headerBytes[count - 1] == '\n') break;
          }

          string headers = Encoding.ASCII.GetString(headerBytes.ToArray());
          int contentLength = 0;
          foreach (string line in headers.Split(new[] { "\r\n" }, StringSplitOptions.None))
          {
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
              int.TryParse(line.Substring("Content-Length:".Length).Trim(), out contentLength);
          }

          int remaining = contentLength;
          byte[] bodyBuffer = new byte[1024];
          while (remaining > 0)
          {
            int read = stream.Read(bodyBuffer, 0, Math.Min(bodyBuffer.Length, remaining));
            if (read <= 0) return false;
            remaining -= read;
          }

          requestReceived.Set();
          if (!allowResponse.WaitOne(10000)) return false;

          string status = statusCode == 200 ? "200 OK" : "500 Internal Server Error";
          byte[] body = Encoding.ASCII.GetBytes("done");
          byte[] responseHeader = Encoding.ASCII.GetBytes(
            "HTTP/1.1 " + status + "\r\nContent-Length: " + body.Length
            + "\r\nConnection: close\r\n\r\n");
          stream.Write(responseHeader, 0, responseHeader.Length);
          stream.Write(body, 0, body.Length);
          stream.Flush();
          return true;
        }
      }
      catch (Exception)
      {
        return false;
      }
    }

    public void Dispose()
    {
      allowResponse.Set();
      if (listener != null) listener.Stop();
      if (requestTask != null && !requestTask.IsCompleted)
      {
        try { requestTask.Wait(1000); }
        catch (AggregateException) { }
      }
      requestReceived.Dispose();
      allowResponse.Dispose();
    }
  }
}
#endif
