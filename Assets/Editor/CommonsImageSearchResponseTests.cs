#if UNITY_EDITOR
using System;
using System.Reflection;
using NUnit.Framework;

public class CommonsImageSearchResponseTests
{
  [Test]
  public void SortPagesOrdersValidPagesAndPlacesNullEntriesLast()
  {
    BindingFlags nestedTypeFlags = BindingFlags.NonPublic;
    BindingFlags privateStatic = BindingFlags.Static | BindingFlags.NonPublic;
    Type managerType = typeof(CommonsImageSearchManager);
    Type pageType = managerType.GetNestedType("CommonsPage", nestedTypeFlags);
    MethodInfo sortPages = managerType.GetMethod("SortPages", privateStatic);
    Assert.IsNotNull(pageType);
    Assert.IsNotNull(sortPages);

    FieldInfo indexField = pageType.GetField("index", BindingFlags.Instance | BindingFlags.Public);
    Assert.IsNotNull(indexField);
    Array pages = Array.CreateInstance(pageType, 4);
    pages.SetValue(CreatePage(pageType, indexField, 9), 0);
    pages.SetValue(null, 1);
    pages.SetValue(CreatePage(pageType, indexField, 2), 2);
    pages.SetValue(CreatePage(pageType, indexField, 5), 3);

    sortPages.Invoke(null, new object[] { pages });

    Assert.AreEqual(2, indexField.GetValue(pages.GetValue(0)));
    Assert.AreEqual(5, indexField.GetValue(pages.GetValue(1)));
    Assert.AreEqual(9, indexField.GetValue(pages.GetValue(2)));
    Assert.IsNull(pages.GetValue(3));
  }

  static object CreatePage(Type pageType, FieldInfo indexField, int index)
  {
    object page = Activator.CreateInstance(pageType, true);
    indexField.SetValue(page, index);
    return page;
  }
}
#endif
