#if UNITY_EDITOR
using System;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

public class ActorPrefabThumbnailLifecycleTests
{
  const BindingFlags InstanceMethods = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

  [Test]
  public void SceneActorLibraryReusesAndRetiresThumbnailUntilTeardown()
  {
    GameObject owner = new GameObject("Scene actor thumbnail test");
    owner.SetActive(false);
    SceneActorLibrary library = owner.AddComponent<SceneActorLibrary>();
    string encodedThumbnail = CreateEncodedThumbnail();
    Texture2D original = null;
    Texture2D replacement = null;

    try
    {
      const string firstRevision = "sojo revision one";
      original = (Texture2D)Invoke(
        library, "GetOrCreateThumbnail", "actor-1", firstRevision, encodedThumbnail);
      Texture2D repeated = (Texture2D)Invoke(
        library, "GetOrCreateThumbnail", "actor-1", firstRevision, encodedThumbnail);
      Assert.AreSame(original, repeated, "Repeated searches must reuse the library-owned decoded thumbnail.");

      string toSojo = (string)typeof(SceneActorLibrary).GetMethod(
        "ToSojoId", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { "actor-1" });
      Invoke(library, "OnSojoPut", new Sojo(toSojo, "actor", SojoType.ActorPrefab, firstRevision));
      Texture2D afterDuplicatePut = (Texture2D)Invoke(
        library, "GetOrCreateThumbnail", "actor-1", firstRevision, encodedThumbnail);
      Assert.AreSame(original, afterDuplicatePut,
        "The local Put event echoed over the network must not decode a duplicate thumbnail.");

      const string secondRevision = "sojo revision two";
      Invoke(library, "OnSojoPut", new Sojo(toSojo, "actor", SojoType.ActorPrefab, secondRevision));
      Assert.IsTrue(original != null, "Existing search results may still reference a retired thumbnail.");
      replacement = (Texture2D)Invoke(
        library, "GetOrCreateThumbnail", "actor-1", secondRevision, encodedThumbnail);
      Assert.IsTrue(replacement != null);
      Assert.IsFalse(object.ReferenceEquals(original, replacement));
      Invoke(library, "OnSojoDelete", new Sojo(toSojo, "actor", SojoType.ActorPrefab, secondRevision));

      Invoke(library, "ReleaseThumbnailTextures");
      Assert.IsTrue(original == null, "Teardown must release retired thumbnail revisions.");
      Assert.IsTrue(replacement == null, "Teardown must release the current thumbnail.");
      original = null;
      replacement = null;
    }
    finally
    {
      if (original != null) UnityEngine.Object.DestroyImmediate(original);
      if (replacement != null) UnityEngine.Object.DestroyImmediate(replacement);
      UnityEngine.Object.DestroyImmediate(owner);
    }
  }

  [Test]
  public void BuiltinPrefabLibraryReusesAndReleasesDecodedThumbnail()
  {
    string subdirectory = "GameBuilderThumbnailTest-" + Guid.NewGuid().ToString("N");
    string libraryRoot = Path.Combine(Util.AbstractLocation.PersistentData.GetConcretePath(), subdirectory);
    string actorPrefabsRoot = Path.Combine(libraryRoot, "ActorPrefabs");
    string id = "actor-" + Guid.NewGuid().ToString("N");
    Directory.CreateDirectory(actorPrefabsRoot);

    GameObject owner = new GameObject("Built-in prefab thumbnail test");
    owner.SetActive(false);
    PrefabLibrary library = owner.AddComponent<PrefabLibrary>();
    library.rootLocation = Util.AbstractLocation.PersistentData;
    library.subdirectory = subdirectory;
    Texture2D first = null;

    try
    {
      string encodedThumbnail = CreateEncodedThumbnail();
      File.WriteAllText(
        Path.Combine(actorPrefabsRoot, id + ".actor-prefab.voos"),
        JsonUtility.ToJson(CreateSavedActorPrefab(id, encodedThumbnail)));

      first = library.Get(id).GetThumbnail();
      Texture2D repeated = library.Get(id).GetThumbnail();
      Assert.AreSame(first, repeated, "Repeated prefab loads must share one library-owned thumbnail.");

      Invoke(library, "ReleaseThumbnailTextures");
      Assert.IsTrue(first == null, "The prefab library must release its decoded thumbnails at teardown.");
      first = null;
    }
    finally
    {
      if (first != null) UnityEngine.Object.DestroyImmediate(first);
      UnityEngine.Object.DestroyImmediate(owner);
      if (Directory.Exists(libraryRoot)) Directory.Delete(libraryRoot, true);
    }
  }

  [Test]
  public void ScriptSpawnLoadsPrefabWithoutDecodingItsThumbnail()
  {
    string subdirectory = "GameBuilderThumbnailTest-" + Guid.NewGuid().ToString("N");
    string libraryRoot = Path.Combine(Util.AbstractLocation.PersistentData.GetConcretePath(), subdirectory);
    string actorPrefabsRoot = Path.Combine(libraryRoot, "ActorPrefabs");
    string id = "actor-" + Guid.NewGuid().ToString("N");
    Directory.CreateDirectory(actorPrefabsRoot);

    GameObject owner = new GameObject("Script prefab spawn thumbnail test");
    owner.SetActive(false);
    PrefabLibrary library = owner.AddComponent<PrefabLibrary>();
    library.rootLocation = Util.AbstractLocation.PersistentData;
    library.subdirectory = subdirectory;

    try
    {
      SavedActorPrefab saved = CreateSavedActorPrefab(
        id, "not base64: spawning does not need this image");
      File.WriteAllText(Path.Combine(actorPrefabsRoot, id + ".actor-prefab.voos"), JsonUtility.ToJson(saved));

      ActorPrefab prefab = (ActorPrefab)Invoke(library, "GetForInstantiation", id);
      Assert.IsNotNull(prefab);
      Assert.IsNull(prefab.GetThumbnail(), "Script-driven instantiation must skip thumbnail decoding.");
    }
    finally
    {
      Invoke(library, "ReleaseThumbnailTextures");
      UnityEngine.Object.DestroyImmediate(owner);
      if (Directory.Exists(libraryRoot)) Directory.Delete(libraryRoot, true);
    }
  }

  static SavedActorPrefab CreateSavedActorPrefab(string id, string thumbnailJZB64)
  {
    return new SavedActorPrefab
    {
      version = SavedActorPrefab.CurrentVersion,
      label = id,
      actors = new[] { new VoosActor.PersistedState() },
      brainDatabase = new Behaviors.Database().Save(),
      sojos = new Sojo.Saved[0],
      thumbnailJZB64 = thumbnailJZB64
    };
  }


  static string CreateEncodedThumbnail()
  {
    Texture2D source = new Texture2D(2, 2, TextureFormat.RGBA32, false);
    try
    {
      source.SetPixels(new[] { Color.red, Color.green, Color.blue, Color.white });
      source.Apply();
      return Convert.ToBase64String(Util.TextureToZippedJpeg(source));
    }
    finally
    {
      UnityEngine.Object.DestroyImmediate(source);
    }
  }

  static object Invoke(object target, string methodName, params object[] arguments)
  {
    MethodInfo method = target.GetType().GetMethod(methodName, InstanceMethods);
    Assert.IsNotNull(method, "Missing lifecycle method: " + methodName);
    return method.Invoke(target, arguments);
  }
}
#endif
