#if UNITY_EDITOR
using System;
using System.Collections;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.TestTools;

public class AssetSearchCancellationTests
{
  static readonly BindingFlags InstanceMethods = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
  static readonly BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

  [UnityTest]
  public IEnumerator DisposingSearchAbortsItsRequestsWithoutCancelingAnotherSearch()
  {
    using (UnityTestHttpServer searchServer = new UnityTestHttpServer(200))
    using (UnityTestHttpServer thumbnailServer = new UnityTestHttpServer(200))
    using (UnityTestHttpServer otherSearchServer = new UnityTestHttpServer(200))
    {
      searchServer.Start();
      thumbnailServer.Start();
      otherSearchServer.Start();

      Type operationType = GetOperationType();
      object cancelledOperation = Activator.CreateInstance(operationType, true);
      object otherOperation = Activator.CreateInstance(operationType, true);
      MethodInfo registerRequest = operationType.GetMethod("RegisterRequest", InstanceMethods);
      MethodInfo unregisterRequest = operationType.GetMethod("UnregisterRequest", InstanceMethods);
      Assert.IsNotNull(registerRequest);
      Assert.IsNotNull(unregisterRequest);

      using (UnityWebRequest searchRequest = UnityWebRequest.Get(searchServer.Url))
      using (UnityWebRequest thumbnailRequest = UnityWebRequest.Get(thumbnailServer.Url))
      using (UnityWebRequest otherSearchRequest = UnityWebRequest.Get(otherSearchServer.Url))
      {
        try
        {
          Assert.IsTrue((bool)registerRequest.Invoke(cancelledOperation, new object[] { searchRequest }));
          Assert.IsTrue((bool)registerRequest.Invoke(cancelledOperation, new object[] { thumbnailRequest }));
          Assert.IsTrue((bool)registerRequest.Invoke(otherOperation, new object[] { otherSearchRequest }));
          UnityWebRequestAsyncOperation searchAsync = searchRequest.SendWebRequest();
          UnityWebRequestAsyncOperation thumbnailAsync = thumbnailRequest.SendWebRequest();
          UnityWebRequestAsyncOperation otherSearchAsync = otherSearchRequest.SendWebRequest();

          for (int frame = 0; frame < 120
            && (!searchServer.RequestReceived || !thumbnailServer.RequestReceived || !otherSearchServer.RequestReceived);
            frame++) yield return null;
          Assert.IsTrue(searchServer.RequestReceived, "The search endpoint did not receive its request.");
          Assert.IsTrue(thumbnailServer.RequestReceived, "The thumbnail endpoint did not receive its request.");
          Assert.IsTrue(otherSearchServer.RequestReceived, "The independent search endpoint did not receive its request.");

          ((IDisposable)cancelledOperation).Dispose();
          searchServer.AllowResponse();
          thumbnailServer.AllowResponse();
          otherSearchServer.AllowResponse();

          for (int frame = 0; frame < 120
            && (!searchRequest.isDone || !thumbnailRequest.isDone || !otherSearchRequest.isDone
              || !searchServer.Finished || !thumbnailServer.Finished || !otherSearchServer.Finished);
            frame++) yield return null;

          Assert.IsTrue(searchAsync.isDone, "Canceling a search must finish its API request.");
          Assert.IsTrue(thumbnailAsync.isDone, "Canceling a search must finish its thumbnail request.");
          Assert.IsTrue(otherSearchAsync.isDone, "The independent search must finish normally.");
          Assert.AreNotEqual(UnityWebRequest.Result.Success, searchRequest.result,
            "Canceling the search must abort its API request.");
          Assert.AreNotEqual(UnityWebRequest.Result.Success, thumbnailRequest.result,
            "Canceling the search must abort its thumbnail request.");
          Assert.IsTrue(otherSearchServer.Succeeded, "Canceling one search must not abort another search.");

          unregisterRequest.Invoke(cancelledOperation, new object[] { searchRequest });
          unregisterRequest.Invoke(cancelledOperation, new object[] { thumbnailRequest });
          unregisterRequest.Invoke(otherOperation, new object[] { otherSearchRequest });
        }
        finally
        {
          searchServer.AllowResponse();
          thumbnailServer.AllowResponse();
          otherSearchServer.AllowResponse();
          if (!searchRequest.isDone) searchRequest.Abort();
          if (!thumbnailRequest.isDone) thumbnailRequest.Abort();
          if (!otherSearchRequest.isDone) otherSearchRequest.Abort();
          ((IDisposable)cancelledOperation).Dispose();
          ((IDisposable)otherOperation).Dispose();
        }
      }
    }
  }

  [Test]
  public void ClearingOrDestroyingEitherLibraryCancelsItsSearch()
  {
    Type[] libraryTypes = { typeof(RenderableLibrary), typeof(CreationLibrarySidebar) };
    foreach (Type libraryType in libraryTypes)
    {
      GameObject libraryObject = new GameObject(libraryType.Name + " cancellation test");
      libraryObject.SetActive(false);
      Component library = libraryObject.AddComponent(libraryType);
      try
      {
        FieldInfo searchField = libraryType.GetField("webImageSearch", PrivateInstance);
        MethodInfo clearAssets = libraryType.GetMethod("ClearAssets", PrivateInstance);
        MethodInfo onDestroy = libraryType.GetMethod("OnDestroy", PrivateInstance);
        Assert.IsNotNull(searchField);
        Assert.IsNotNull(clearAssets);
        Assert.IsNotNull(onDestroy);

        object clearedSearch = Activator.CreateInstance(GetOperationType(), true);
        searchField.SetValue(library, clearedSearch);
        clearAssets.Invoke(library, null);
        Assert.IsTrue(IsCancelled(clearedSearch), libraryType.Name + " did not cancel on clear.");
        Assert.IsNull(searchField.GetValue(library), libraryType.Name + " retained its cleared search handle.");

        object destroyedSearch = Activator.CreateInstance(GetOperationType(), true);
        searchField.SetValue(library, destroyedSearch);
        onDestroy.Invoke(library, null);
        Assert.IsTrue(IsCancelled(destroyedSearch), libraryType.Name + " did not cancel on teardown.");
        Assert.IsNull(searchField.GetValue(library), libraryType.Name + " retained its teardown search handle.");
      }
      finally
      {
        UnityEngine.Object.DestroyImmediate(libraryObject);
      }
    }
  }

  static Type GetOperationType()
  {
    Type operationType = typeof(CommonsImageSearchManager).GetNestedType(
      "SearchOperation", BindingFlags.NonPublic);
    Assert.IsNotNull(operationType);
    return operationType;
  }

  static bool IsCancelled(object operation)
  {
    PropertyInfo property = GetOperationType().GetProperty("IsCancelled", InstanceMethods);
    Assert.IsNotNull(property);
    return (bool)property.GetValue(operation);
  }
}
#endif
