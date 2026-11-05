#if UNITY_EDITOR && USE_STEAMWORKS
using GameBuilder.Workshop;
using NUnit.Framework;
using Steamworks;
using UnityEngine;

public class SteamWorkshopQuerySettingsTests
{
  GameObject serviceObject;
  SteamWorkshopService service;

  [SetUp]
  public void SetUp()
  {
    service = SteamWorkshopService.Instance;
    if (service == null)
    {
      serviceObject = new GameObject("Workshop query settings test service");
      service = serviceObject.AddComponent<SteamWorkshopService>();
    }
  }

  [TearDown]
  public void TearDown()
  {
    if (serviceObject != null) Object.DestroyImmediate(serviceObject);
  }

  [Test]
  public void ResetItemSearchRestoresLegacyAnyTagSearchDefaults()
  {
    service.SearchText = "old query";
    service.SearchMatchAnyTag = false;
    service.SearchTags.Add("racing");
    service.ExcludedTags.Add("broken");
    service.Sorting = new WorkshopSortMode(EUGCQuery.k_EUGCQuery_RankedByVote);

    service.ResetItemSearch();

    Assert.That(service.SearchText, Is.Empty);
    Assert.That(service.SearchMatchAnyTag, Is.True);
    Assert.That(service.SearchTags, Is.Empty);
    Assert.That(service.ExcludedTags, Is.Empty);
    Assert.That(service.Sorting.Query, Is.EqualTo(EUGCQuery.k_EUGCQuery_RankedByTrend));
  }
}
#endif
