/*
 * Copyright 2019 Google LLC
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     https://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public interface VoosAsset
{
  void Accept(VoosAssetVisitor visitor);
  string GetUri();
}

public class PolyVoosAsset : VoosAsset
{
  public readonly string assetId;

  public PolyVoosAsset(string assetId)
  {
    this.assetId = assetId;
  }

  public void Accept(VoosAssetVisitor visitor)
  {
    visitor.Visit(this);
  }

  public string GetUri()
  {
    return $"poly:{assetId}";
  }
}

public class ImageVoosAsset : VoosAsset
{
  const string ATTRIBUTION_FRAGMENT_PREFIX = "#gbng-attribution=";

  public readonly string url;
  public readonly string attribution;

  public ImageVoosAsset(string uri)
  {
    if (uri == null)
    {
      url = null;
      attribution = null;
      return;
    }

    int attributionIndex = uri.LastIndexOf(ATTRIBUTION_FRAGMENT_PREFIX, System.StringComparison.Ordinal);
    if (attributionIndex < 0)
    {
      url = uri;
      attribution = null;
      return;
    }

    url = uri.Substring(0, attributionIndex);
    attribution = System.Uri.UnescapeDataString(uri.Substring(attributionIndex + ATTRIBUTION_FRAGMENT_PREFIX.Length));
  }

  public ImageVoosAsset(string uri, string attribution)
  {
    this.url = uri;
    this.attribution = attribution;
  }

  public void Accept(VoosAssetVisitor visitor)
  {
    visitor.Visit(this);
  }

  public string GetUri()
  {
    return string.IsNullOrEmpty(attribution)
      ? url
      : url + ATTRIBUTION_FRAGMENT_PREFIX + System.Uri.EscapeDataString(attribution);
  }
}

public class BuiltinVoosAsset : VoosAsset
{
  public readonly string resourcePath;

  public BuiltinVoosAsset(string resourcePath)
  {
    this.resourcePath = resourcePath;
  }

  public void Accept(VoosAssetVisitor visitor)
  {
    visitor.Visit(this);
  }

  public string GetUri()
  {
    return $"builtin:{resourcePath}";
  }
}

public class LocalFbxAsset : VoosAsset
{
  public readonly string absoluteFilePath;

  public LocalFbxAsset(string absPath)
  {
    this.absoluteFilePath = absPath;
  }

  public void Accept(VoosAssetVisitor visitor)
  {
    visitor.Visit(this);
  }

  public string GetUri()
  {
    return $"localfbx:{absoluteFilePath}";
  }
}

public class SteamWorkshopAsset : VoosAsset
{
  public readonly ulong publishedId;

  public SteamWorkshopAsset(ulong id)
  {
    this.publishedId = id;
  }

  public void Accept(VoosAssetVisitor visitor)
  {
    visitor.Visit(this);
  }

  public string GetUri()
  {
    return $"steamworkshop:{publishedId}";
  }
}

public interface VoosAssetVisitor
{
  void Visit(PolyVoosAsset asset);
  void Visit(ImageVoosAsset asset);
  void Visit(BuiltinVoosAsset asset);
  void Visit(LocalFbxAsset asset);
  void Visit(SteamWorkshopAsset asset);
}

public static class VoosAssetUtil
{
  public static VoosAsset AssetFromUri(string uri)
  {
    const string LOCAL_ASSET_PREFIX = "localfbx:";
    if (uri != null && uri.StartsWith(LOCAL_ASSET_PREFIX, System.StringComparison.OrdinalIgnoreCase))
    {
      return new LocalFbxAsset(uri.Substring(LOCAL_ASSET_PREFIX.Length));
    }
    return AssetFromUri(new System.Uri(uri));
  }

  public static VoosAsset AssetFromUri(System.Uri uri)
  {
    if (uri.Scheme == "poly")
    {
      return new PolyVoosAsset(uri.PathAndQuery);
    }
    else if (uri.Scheme == "builtin")
    {
      return new BuiltinVoosAsset(uri.PathAndQuery);
    }
    else if (uri.Scheme == "localfbx")
    {
      return new LocalFbxAsset(uri.PathAndQuery);
    }
    else if (uri.Scheme == "http" || uri.Scheme == "https")
    {
      // Assume the URI is an image URL for now.
      return new ImageVoosAsset(uri.ToString());
    }
    else if (uri.Scheme == "steamworkshop")
    {
      return new SteamWorkshopAsset(System.UInt64.Parse(uri.PathAndQuery));
    }
    else
    {
      throw new System.Exception($"Unknown VoosAsset scheme '{uri.Scheme}' from URI '{uri}'");
    }
  }

  public static bool IsLocalAsset(string uri)
  {
    return !uri.IsNullOrEmpty() &&
      uri.StartsWith("localfbx:", System.StringComparison.OrdinalIgnoreCase);
  }
}