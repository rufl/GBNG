#if UNITY_EDITOR
using System;
using System.Collections;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

public class FeedbackFormLifecycleTests
{
  [UnityTest]
  public IEnumerator SubmissionCompletesAfterFormHostIsDestroyed()
  {
    using (UnityTestHttpServer server = new UnityTestHttpServer(200))
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

}
#endif
