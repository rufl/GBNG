#if UNITY_EDITOR && USE_STEAMWORKS
using System;
using System.IO;
using System.Reflection;
using System.Xml.Linq;
using GameBuilder.Workshop;
using NUnit.Framework;
using UnityEngine;

public class SteamWorkshopMetadataTests
{
  const ulong PublishedFileId = 76561198012345678;

  string contentPath;
  string outsidePreviewPath;
  GameObject serviceObject;
  SteamWorkshopService service;

  [SetUp]
  public void SetUp()
  {
    contentPath = Path.Combine(Path.GetTempPath(), "GameBuilderWorkshopMetadata-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(contentPath);
    service = SteamWorkshopService.Instance;
    if (service == null)
    {
      serviceObject = new GameObject("Workshop metadata test service");
      service = serviceObject.AddComponent<SteamWorkshopService>();
    }
  }

  [TearDown]
  public void TearDown()
  {
    if (serviceObject != null) UnityEngine.Object.DestroyImmediate(serviceObject);
    if (Directory.Exists(contentPath)) Directory.Delete(contentPath, true);
    if (File.Exists(outsidePreviewPath)) File.Delete(outsidePreviewPath);
  }

  [Test]
  public void ReadsLapinerToolsMetadataAndPreviewFile()
  {
    File.WriteAllText(Path.Combine(contentPath, "preview.png"), "preview");
    File.WriteAllText(Path.Combine(contentPath, "WorkshopItemInfo.xml"),
      "<WorkshopItemInfo><PublishedFileId>" + PublishedFileId + "</PublishedFileId>" +
      "<Name>Legacy project</Name><Description>Legacy description</Description>" +
      "<IconFileName>preview.png</IconFileName><Tags><string>Project</string><string>GameBuilder</string></Tags></WorkshopItemInfo>");

    WorkshopItemUpdate update = service.GetItemUpdateFromFolder(contentPath);

    Assert.IsNotNull(update);
    Assert.AreEqual(PublishedFileId, update.PublishedFileId);
    Assert.AreEqual("Legacy project", update.Name);
    Assert.AreEqual("Legacy description", update.Description);
    Assert.AreEqual(Path.Combine(contentPath, "preview.png"), update.IconPath);
    CollectionAssert.AreEqual(new[] { "Project", "GameBuilder" }, update.Tags);
  }

  [Test]
  public void DoesNotResolvePreviewMetadataOutsideContentFolder()
  {
    outsidePreviewPath = Path.Combine(Path.GetDirectoryName(contentPath), "outside-preview.png");
    File.WriteAllText(outsidePreviewPath, "not part of the upload");
    File.WriteAllText(Path.Combine(contentPath, "WorkshopItemInfo.xml"),
      "<WorkshopItemInfo><PublishedFileId>" + PublishedFileId + "</PublishedFileId>" +
      "<IconFileName>../outside-preview.png</IconFileName></WorkshopItemInfo>");

    WorkshopItemUpdate update = service.GetItemUpdateFromFolder(contentPath);

    Assert.IsNotNull(update);
    Assert.IsNull(update.IconPath);
 
  }

  [Test]
  public void RejectsLegacyMetadataWithoutPublishedId()
  {
    File.WriteAllText(Path.Combine(contentPath, "WorkshopItemInfo.xml"),
      "<WorkshopItemInfo><Name>Legacy project</Name><Tags><string>Project</string></Tags></WorkshopItemInfo>");

    Assert.IsNull(service.GetItemUpdateFromFolder(contentPath));
  }

  [Test]
  public void WritesXmlSerializerCompatibleMetadataAndCanReadItBack()
  {
    File.WriteAllText(Path.Combine(contentPath, "preview.png"), "preview");
    var update = new WorkshopItemUpdate
    {
      PublishedFileId = PublishedFileId,
      Name = "Project & game",
      Description = "Legacy-compatible metadata",
      ContentPath = contentPath,
      IconPath = Path.Combine(contentPath, "preview.png")
    };
    update.Tags.Add("Project");
    update.Tags.Add("GameBuilder");
    MethodInfo writeMetadata = typeof(SteamWorkshopService).GetMethod("WriteWorkshopMetadata", BindingFlags.Instance | BindingFlags.NonPublic);

    Assert.IsNotNull(writeMetadata);
    Assert.IsTrue((bool)writeMetadata.Invoke(service, new object[] { update }));

    XDocument document = XDocument.Load(Path.Combine(contentPath, "WorkshopItemInfo.xml"));
    Assert.AreEqual(PublishedFileId.ToString(), document.Root.Element("PublishedFileId").Value);
    Assert.AreEqual("preview.png", document.Root.Element("IconFileName").Value);
    CollectionAssert.AreEqual(new[] { "string", "string" },
      System.Linq.Enumerable.Select(document.Root.Element("Tags").Elements(), element => element.Name.LocalName));
    CollectionAssert.AreEqual(new[] { "Project", "GameBuilder" },
      System.Linq.Enumerable.Select(document.Root.Element("Tags").Elements(), element => element.Value));
    WorkshopItemUpdate readBack = service.GetItemUpdateFromFolder(contentPath);
    Assert.IsNotNull(readBack);
    Assert.AreEqual("Project & game", readBack.Name);
    CollectionAssert.AreEqual(update.Tags, readBack.Tags);
  }
}
#endif
