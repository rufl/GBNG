#if !USE_PUN
using System;
using System.Threading.Tasks;
using Unity.Netcode;
using UnityEngine;

public sealed class DirectHostPortMapper : MonoBehaviour
{
  const float RenewalIntervalSeconds = 1800f;
  const float RenewalRetrySeconds = 60f;

  static DirectHostPortMapper instance;
  static bool mappingResolved = true;
  static ushort publicPort = NetworkingController.DefaultDirectHostPort;
  static string statusMessage = "Automatic router mapping has not been attempted.";

  NetworkManager manager;
  bool hosting;
  bool pendingMapping;
  bool renewalWarningLogged;
  int generation;
  int mappingGeneration;
  int renewalGeneration;
  Task<UpnpIgdPortMapper.MappingResult> mappingTask;
  Task<string> renewalTask;
  Task<string> cleanupTask;
  UpnpIgdPortMapper.Mapping currentMapping;
  float nextRenewalTime;

  public static bool IsMappingResolved { get { return mappingResolved; } }
  public static ushort PublicPort { get { return publicPort; } }
  public static string StatusMessage { get { return statusMessage; } }

  public static void EnsureAvailable()
  {
    EnsureInstance();
  }

  public static void BeginHostMapping()
  {
    if (NetworkingController.IsDirectIpv6Host) return;
    EnsureInstance().BeginMappingInternal();
  }

  static DirectHostPortMapper EnsureInstance()
  {
    if (instance != null) return instance;
    GameObject serviceObject = new GameObject("DirectHostPortMapper");
    DontDestroyOnLoad(serviceObject);
    return serviceObject.AddComponent<DirectHostPortMapper>();
  }

  void Awake()
  {
    if (instance != null && instance != this)
    {
      Destroy(gameObject);
      return;
    }
    instance = this;
    manager = NetworkManager.Singleton;
    if (IsHosting(manager)) BeginMappingInternal();
  }

  void Update()
  {
    if (manager == null) manager = NetworkManager.Singleton;
    bool shouldHost = IsHosting(manager);
    if (shouldHost && !hosting)
      BeginMappingInternal();
    else if (!shouldHost && hosting)
      StopMappingInternal();

    CompleteMapping();
    CompleteCleanup();
    CompleteRenewal();
    if (hosting && currentMapping != null && currentMapping.LeaseSeconds > 0
      && renewalTask == null && Time.realtimeSinceStartup >= nextRenewalTime)
    {
      StartRenewal();
    }
    StartPendingMapping();
  }

  static bool IsHosting(NetworkManager value)
  {
    return value != null && value.IsListening && value.IsServer
      && !NetworkingController.IsDirectIpv6Host;
  }

  void BeginMappingInternal()
  {
    if (hosting) return;
    hosting = true;
    generation++;
    pendingMapping = true;
    mappingResolved = false;
    publicPort = NetworkingController.DefaultDirectHostPort;
    statusMessage = "Checking for an UPnP router...";
    StartPendingMapping();
  }

  void StartPendingMapping()
  {
    if (!hosting || !pendingMapping || mappingTask != null) return;
    if (cleanupTask != null && !cleanupTask.IsCompleted) return;
    pendingMapping = false;
    mappingGeneration = generation;
    mappingTask = Task.Run(() => UpnpIgdPortMapper.TryCreate(NetworkingController.DefaultDirectHostPort));
  }

  void CompleteMapping()
  {
    if (mappingTask == null || !mappingTask.IsCompleted) return;
    Task<UpnpIgdPortMapper.MappingResult> completedTask = mappingTask;
    mappingTask = null;

    UpnpIgdPortMapper.MappingResult result;
    try
    {
      result = completedTask.GetAwaiter().GetResult();
    }
    catch (Exception e)
    {
      result = new UpnpIgdPortMapper.MappingResult(null, "UPnP mapping failed: " + e.Message);
    }

    if (!hosting || mappingGeneration != generation)
    {
      if (result.Mapping != null) ScheduleDelete(result.Mapping, null);
      if (hosting) pendingMapping = true;
      return;
    }

    currentMapping = result.Mapping;
    publicPort = currentMapping == null
      ? NetworkingController.DefaultDirectHostPort
      : currentMapping.ExternalPort;
    mappingResolved = true;
    statusMessage = result.Message;
    renewalWarningLogged = false;
    if (currentMapping == null)
    {
      Debug.LogWarning(statusMessage + " Forward UDP port "
        + NetworkingController.DefaultDirectHostPort + " manually if Internet joins fail.");
    }
    else
    {
      Debug.Log(statusMessage);
      nextRenewalTime = currentMapping.LeaseSeconds == 0
        ? float.PositiveInfinity
        : Time.realtimeSinceStartup + RenewalIntervalSeconds;
    }
  }

  void StartRenewal()
  {
    renewalGeneration = generation;
    UpnpIgdPortMapper.Mapping lease = currentMapping;
    renewalTask = Task.Run(() => UpnpIgdPortMapper.Renew(lease));
  }

  void CompleteRenewal()
  {
    if (renewalTask == null || !renewalTask.IsCompleted) return;
    Task<string> completedTask = renewalTask;
    renewalTask = null;
    string error;
    try
    {
      error = completedTask.GetAwaiter().GetResult();
    }
    catch (Exception e)
    {
      error = e.Message;
    }
    if (!hosting || renewalGeneration != generation || currentMapping == null) return;

    if (string.IsNullOrEmpty(error))
    {
      nextRenewalTime = Time.realtimeSinceStartup + RenewalIntervalSeconds;
      renewalWarningLogged = false;
    }
    else
    {
      nextRenewalTime = Time.realtimeSinceStartup + RenewalRetrySeconds;
      if (!renewalWarningLogged)
      {
        Debug.LogWarning("Could not renew the UPnP UDP mapping; retrying: " + error);
        renewalWarningLogged = true;
      }
    }
  }

  void StopMappingInternal()
  {
    if (!hosting && currentMapping == null && mappingResolved) return;
    hosting = false;
    pendingMapping = false;
    generation++;
    mappingResolved = true;
    publicPort = NetworkingController.DefaultDirectHostPort;
    statusMessage = "Host stopped; router mapping cleanup requested.";

    UpnpIgdPortMapper.Mapping lease = currentMapping;
    currentMapping = null;
    Task<string> activeRenewal = renewalTask;
    renewalTask = null;
    if (lease != null) ScheduleDelete(lease, activeRenewal);
  }

  void ScheduleDelete(UpnpIgdPortMapper.Mapping lease, Task<string> waitForRenewal)
  {
    Task<string> earlierCleanup = cleanupTask;
    cleanupTask = Task.Run(() =>
    {
      WaitQuietly(earlierCleanup);
      WaitQuietly(waitForRenewal);
      return UpnpIgdPortMapper.Delete(lease);
    });
  }

  static void WaitQuietly(Task task)
  {
    if (task == null) return;
    try { task.GetAwaiter().GetResult(); }
    catch (Exception) { }
  }

  void CompleteCleanup()
  {
    if (cleanupTask == null || !cleanupTask.IsCompleted) return;
    Task<string> completedTask = cleanupTask;
    cleanupTask = null;
    string error;
    try
    {
      error = completedTask.GetAwaiter().GetResult();
    }
    catch (Exception e)
    {
      error = e.Message;
    }
    if (!string.IsNullOrEmpty(error))
      Debug.LogWarning("Could not remove the UPnP UDP mapping; it expires automatically: " + error);
  }

  void OnApplicationQuit()
  {
    StopMappingInternal();
  }

  void OnDestroy()
  {
    if (instance != this) return;
    StopMappingInternal();
    instance = null;
  }
}
#endif
