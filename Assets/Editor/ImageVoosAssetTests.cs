#if UNITY_EDITOR
using System.Reflection;
using NUnit.Framework;

public class ImageVoosAssetTests
{
  [Test]
  public void AttributionSurvivesImageUriSaveAndLoad()
  {
    const string imageUrl = "https://upload.wikimedia.org/wikipedia/commons/a/ab/Commons_test.jpg?download=1";
    const string attribution = "File:Commons test.jpg — by A & B — CC BY 4.0 — https://creativecommons.org/licenses/by/4.0/ — https://commons.wikimedia.org/wiki/File:Commons_test.jpg";
    string savedUri = new ImageVoosAsset(imageUrl, attribution).GetUri();

    ImageVoosAsset loadedAsset = VoosAssetUtil.AssetFromUri(savedUri) as ImageVoosAsset;

    Assert.IsNotNull(loadedAsset);
    Assert.AreEqual(imageUrl, loadedAsset.url);
    Assert.AreEqual(attribution, loadedAsset.attribution);
    Assert.AreEqual(savedUri, loadedAsset.GetUri());
  }

  [Test]
  public void CommonsSearchAllowsAttributionAndPublicDomainLicensesOnly()
  {
    Assert.IsTrue(IsAllowedLicense("Public domain"));
    Assert.IsTrue(IsAllowedLicense("CC0"));
    Assert.IsTrue(IsAllowedLicense("CC BY 4.0"));
    Assert.IsFalse(IsAllowedLicense("CC BY-SA 4.0"));
    Assert.IsFalse(IsAllowedLicense("CC BY-NC 4.0"));
    Assert.IsFalse(IsAllowedLicense("CC BY-ND 4.0"));
    Assert.IsFalse(IsAllowedLicense("GFDL"));
    Assert.IsFalse(IsAllowedLicense(null));
  }

  static bool IsAllowedLicense(string licenseName)
  {
    MethodInfo method = typeof(CommonsImageSearchManager).GetMethod(
      "IsAllowedLicense", BindingFlags.NonPublic | BindingFlags.Static);
    Assert.IsNotNull(method);
    return (bool)method.Invoke(null, new object[] { licenseName });
  }

  [Test]
  public void LegacyImageUrisAndTheirFragmentsRemainUnchanged()
  {
    const string legacyUri = "https://example.org/image.png#legacy-anchor";
    ImageVoosAsset asset = VoosAssetUtil.AssetFromUri(legacyUri) as ImageVoosAsset;
    Assert.IsNotNull(asset);

    Assert.AreEqual(legacyUri, asset.url);
    Assert.IsNull(asset.attribution);
    Assert.AreEqual(legacyUri, asset.GetUri());
  }
}
#endif
