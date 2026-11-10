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
using System.Linq;
using System;

[RequireComponent(typeof(PhotonView))]
public class UserBody : Photon.MonoBehaviour, PlayerBody.EventHandler, IPunObservable
{
  [System.Serializable]
  public struct CreatePreviewSettings
  {
    public RenderableReference renderable;
    public Vector3 renderableOffset;
    public Quaternion renderableRotation;
    public Quaternion addlRotation;
    public Vector3 scale;
  }

  public interface NetworkableToolEffect
  {
    void SetActive(bool active);
    void OnLateUpdate();
    void SetSpatialAudio(bool enabled);
    void SetRayOriginTransform(Transform origin);
    void SetTint(Color tint);
    GameObject GetGameObject();

    void SetTargetPosition(Vector3 position);
    void SetReceivedTargetActor(VoosActor actor);
  }

  public Transform toolEmissionAnchor;
  public Transform thirdPersonCameraPivotAnchor;
  [SerializeField] Animator animator;
  [SerializeField] Transform playAvatar;
  [SerializeField] Transform editAvatar;
  [SerializeField] AvatarAudioController avatarAudioController;
  [SerializeField] GameBuilderStage stage;
  [SerializeField] AvatarMaterialControl avatarMaterialControl;
  [SerializeField] CreateToolPreview createPreviewPrefab;

  public Transform playAvatarWheel;

  MeshRenderer[] playAvatarRenderers;

  [SerializeField] MeshRenderer[] renderersForTint;
  [SerializeField] MeshRenderer[] variableTransparencyRenderers;
  Dictionary<MeshRenderer, Material> tintMaterialByRenderer;
  List<Material> ownedTintMaterials;

  bool variableTransparencyRenderersVisible = true;
  bool playAvatarRendererEnabled = true;
  public Color currentTint;
  bool inPlayMode = true;
  // NOTE: Not really happy with tool effect networking..but it works.
  [SerializeField] GameObject[] toolEffectPrefabs;

  [System.Serializable]
  public struct GroundEffect
  {
    public GameBuilderStage.GroundType groundType;
    public GameObject effectObject;
  }
  [SerializeField] GroundEffect[] groundEffects;

  // Set non-null if this is a local user body and the local user is using this tool.
  Tool locallyActiveTool = null;

  // Only relevant to remote bodies
  NetworkableToolEffect toolEffectReplicant = null;
  string currentToolEffectName = null;
  int receivedToolTargetViewId;

  // Networking create preview
  string lastCreatePreviewSettingsJson = null;
  CreateToolPreview createPreviewInst = null;

  Vector3 lastReceivedTargetPos = Vector3.zero;
  Vector3 lerpedTargetPos = Vector3.positiveInfinity;

  bool isLocalPlayer = false;
  bool setupCalled = false;

  Vector3 velocityForMultiplayer = Vector3.zero;
  Vector3 lastPosForMultiplayer = Vector3.zero;
#if !USE_PUN
  Vector3 directTargetPosition;
  Quaternion directTargetRotation;
  bool hasDirectTargetPose;
  float nextDirectPoseTime;
#if !USE_PUN
  int directDamageSequence;
  int directDeathSequence;
  int directRespawnSequence;
  int receivedDirectDamageSequence;
  int receivedDirectDeathSequence;
  int receivedDirectRespawnSequence;
#endif
#endif

  public void SetActiveTool(Tool tool)
  {
    locallyActiveTool = tool;
  }

  void Awake()
  {
    Util.FindIfNotSet(this, ref stage);
    playAvatarRenderers = playAvatar.GetComponentsInChildren<MeshRenderer>(true /* include inactive children */);
    OnUpdateGroundType();
  }

  void OnEnable()
  {
    stage.OnUpdateGroundType += OnUpdateGroundType;
  }

  void OnDisable()
  {
    stage.OnUpdateGroundType -= OnUpdateGroundType;
  }

  void OnUpdateGroundType()
  {
    foreach (var effect in groundEffects)
    {
      effect.effectObject.SetActive(effect.groundType == stage.GetGroundType());
    }
  }

  public void UpdateVelocity(Vector2 velocity)
  {
    if (isLocalPlayer)
    {
      animator.SetFloat("VelX", velocity.x, 0.5f, Time.unscaledDeltaTime * 7.5f);
      animator.SetFloat("VelY", velocity.y, 0.5f, Time.unscaledDeltaTime * 7.5f);
    }
    avatarAudioController.UpdateVelocity(velocity.x, velocity.y);
  }

  public void SetIsPlayingAsRobot(bool val)
  {
    avatarAudioController.SetIsPlayingAsRobot(val);
  }

  public void SetInPlayMode(bool value)
  {
    if (inPlayMode != value)
    {
      inPlayMode = value;
      OnInPlayModeChanged();
    }
  }


  void UpdateReplicantToolEffectTargetPos()
  {
    if (Single.IsInfinity(lastReceivedTargetPos.x)) return;

    if (Single.IsInfinity(lerpedTargetPos.x))
    {
      lerpedTargetPos = lastReceivedTargetPos;
    }
    else
    {
      lerpedTargetPos = Vector3.Lerp(lerpedTargetPos, lastReceivedTargetPos, 0.5f);
    }

    toolEffectReplicant?.SetTargetPosition(lerpedTargetPos);

    if (createPreviewInst != null)
    {
      createPreviewInst.transform.position = lerpedTargetPos;
    }
  }

  void Update()
  {
#if !USE_PUN
    if (isLocalPlayer)
    {
      if (!PhotonNetwork.offlineMode && Time.unscaledTime >= nextDirectPoseTime)
      {
        nextDirectPoseTime = Time.unscaledTime + 0.05f;
        PhotonView view = GetComponent<PhotonView>();
        if (view != null) DirectHostRpcRouter.SendUserBodyPose(view, transform.position, transform.rotation);
      }
    }
    else if (hasDirectTargetPose)
    {
      float blend = 1f - Mathf.Exp(-18f * Time.unscaledDeltaTime);
      transform.position = Vector3.Lerp(transform.position, directTargetPosition, blend);
      transform.rotation = Quaternion.Slerp(transform.rotation, directTargetRotation, blend);
    }
#endif

    if (!isLocalPlayer)
    {
      //find velocity
      //Vector3 velocityForMultiplayer = Vector3.Lerp( Vector3.zero;
      velocityForMultiplayer = Vector3.Lerp(velocityForMultiplayer, (transform.position - lastPosForMultiplayer) / Time.unscaledDeltaTime, .1f);
      Vector3 relativeVelocity = Quaternion.Inverse(Quaternion.LookRotation(transform.forward)) * velocityForMultiplayer;

      //makes it smaller for the animator
      float x = relativeVelocity.x / NavigationControls.MOVE_SPEED;
      float y = relativeVelocity.z / NavigationControls.MOVE_SPEED;
      UpdateVelocity(new Vector2(x, y));

      lastPosForMultiplayer = transform.position;

      UpdateReplicantToolEffectTargetPos();
    }
  }

  void SetGroundEffectsEmission(bool enabled)
  {
    foreach (var effect in groundEffects)
    {
      var particles = effect.effectObject.GetComponentInChildren<ParticleSystem>(true);
      if (particles != null)
      {
        var em = particles.emission;
        em.enabled = enabled;
      }
    }
  }

  void OnInPlayModeChanged()
  {
    if (this == null
      || animator == null
      || avatarMaterialControl == null
      || avatarAudioController == null)
    {
      return;
    }

    if (inPlayMode)
    {
      avatarMaterialControl.SetToStandardMaterial();
      animator.ResetTrigger("TransformToEditor");
      animator.SetTrigger("TransformToExplorer");
      inPlayMode = true;
      avatarAudioController.OnTransformToExplorer();
      SetGroundEffectsEmission(true && animator.GetBool("IsGrounded"));
      DestroyToolEffectReplicant();
    }
    else
    {
      avatarMaterialControl.SetToHologramMaterial();
      animator.ResetTrigger("TransformToExplorer");
      animator.SetTrigger("TransformToEditor");
      inPlayMode = false;
      avatarAudioController.OnTransformToEditor();
      SetGroundEffectsEmission(false);
    }
  }

  public void SetHologramMenu(bool on)
  {
    // Silence what is probably an on-exit only crash.
    // DO NOT USE ?. !!!
    if (animator == null) return;
    animator.SetBool("EditorHolograms", on);
  }

  public void SetCrouch(bool on)
  {
    if (on != animator.GetBool("IsCrouching"))
    {
      animator.SetBool("IsCrouching", on);
    }
  }

  public void SetGrounded(bool on)
  {
    if (on != animator.GetBool("IsGrounded"))
    {
      animator.SetBool("IsGrounded", on);
      avatarAudioController.UpdateGrounded(on);

      SetGroundEffectsEmission(on && inPlayMode);
    }
  }

  public void SetSprint(bool on)
  {
    if (on != animator.GetBool("IsSprinting"))
    {
      animator.SetBool("IsSprinting", on);
    }
  }

  public void SetVariableTransparency(float value)
  {
    if (variableTransparencyRenderersVisible == (value != 0)) return;

    variableTransparencyRenderersVisible = value != 0;

    for (int i = 0; i < variableTransparencyRenderers.Length; i++)
    {
      variableTransparencyRenderers[i].enabled = variableTransparencyRenderersVisible && playAvatarRendererEnabled;
    }
  }

  public void SetPlayerVisible(bool on)
  {
    if (playAvatarRendererEnabled == on) return;

    playAvatarRendererEnabled = on;

    for (int i = 0; i < playAvatarRenderers.Length; i++)
    {
      //only set it if its not in the variable transparency thing - those are done in the second for loop
      if (!variableTransparencyRenderers.Contains(playAvatarRenderers[i]))
        playAvatarRenderers[i].enabled = on;
    }

    for (int i = 0; i < variableTransparencyRenderers.Length; i++)
    {
      variableTransparencyRenderers[i].enabled = on && variableTransparencyRenderersVisible;
    }
  }

  public void OnDamaged()
  {
#if !USE_PUN
    directDamageSequence++;
#endif
    animator.SetTrigger("IsDamaged");
    avatarAudioController.OnDamage();
  }

  public void OnDied()
  {
#if !USE_PUN
    directDeathSequence++;
#endif
    animator.SetTrigger("IsDead");
    avatarAudioController.OnDeath();

  }

  public void OnJumpDenied()
  {
    //animator.SetTrigger("IsJumping");
  }

  public void OnJumped()
  {
    avatarAudioController.OnJump();
    //animator.SetTrigger("IsJumping");
  }

  public void OnLanded()
  {
    //animator.SetBool("IsGrounded", true);
  }

  public void OnRespawned()
  {
#if !USE_PUN
    directRespawnSequence++;
#endif
    animator.SetTrigger("IsRespawning");
    avatarAudioController.OnRespawn();
  }

  public void SetTint(Color newTint)
  {
    if (currentTint != newTint)
    {
      currentTint = newTint;
      OnTintChanged();
    }
  }

  public void OnTintChanged()
  {
    foreach (MeshRenderer tintRenderer in renderersForTint)
    {
      Material tintMaterial = GetOrCreateTintMaterial(tintRenderer);
      if (tintMaterial != null) tintMaterial.SetColor("_MainTint", currentTint);
    }
    avatarMaterialControl.SetTint(currentTint);

    toolEffectReplicant?.SetTint(currentTint);
    createPreviewInst?.SetTint(currentTint);
  }

  Material GetOrCreateTintMaterial(MeshRenderer tintRenderer)
  {
    if (tintRenderer == null || tintRenderer.sharedMaterial == null) return null;
    if (tintMaterialByRenderer == null)
    {
      tintMaterialByRenderer = new Dictionary<MeshRenderer, Material>();
    }

    Material tintMaterial;
    if (tintMaterialByRenderer.TryGetValue(tintRenderer, out tintMaterial) &&
        tintRenderer.sharedMaterial == tintMaterial)
    {
      return tintMaterial;
    }

    if (tintMaterial != null)
    {
      ownedTintMaterials.Remove(tintMaterial);
      RuntimeModelResourceSet.DestroyResource(tintMaterial);
    }

    tintMaterial = new Material(tintRenderer.sharedMaterial);
    tintRenderer.sharedMaterial = tintMaterial;
    tintMaterialByRenderer[tintRenderer] = tintMaterial;
    if (ownedTintMaterials == null) ownedTintMaterials = new List<Material>();
    ownedTintMaterials.Add(tintMaterial);
    return tintMaterial;
  }

  void IPunObservable.OnPhotonSerializeView(PhotonStream stream, PhotonMessageInfo info)
  {
    if (stream.isWriting)
    {
      stream.SendNext(inPlayMode);
      stream.SendColor(currentTint);

      bool toolActive = locallyActiveTool != null;
      stream.SendNext(toolActive);
      if (toolActive)
      {
        stream.SendNext(locallyActiveTool.GetToolEffectName());
        stream.SendNext(locallyActiveTool.GetToolEffectActive());
        stream.SendNext(locallyActiveTool.GetToolEffectTargetViewId());
        stream.SendNext(locallyActiveTool.GetToolEffectTargetPosition());
        stream.SendNext(locallyActiveTool.GetCreatePreviewSettingsJson());
      }
      stream.SendNext(playAvatarRendererEnabled);
#if !USE_PUN
      stream.SendNext(animator.GetBool("IsCrouching"));
      stream.SendNext(animator.GetBool("IsGrounded"));
      stream.SendNext(animator.GetBool("IsSprinting"));
      stream.SendNext(animator.GetBool("EditorHolograms"));
      stream.SendNext(animator.GetFloat("VelX"));
      stream.SendNext(animator.GetFloat("VelY"));
      stream.SendNext(directDamageSequence);
      stream.SendNext(directDeathSequence);
      stream.SendNext(directRespawnSequence);
#endif
    }
    else
    {
      if (!setupCalled)
      {
        Setup(false);
      }

      SetInPlayMode((bool)stream.ReceiveNext());
      SetTint(stream.ReceiveColor());
      if ((bool)stream.ReceiveNext())
      {
        // Tool is active.
        SetToolEffectName((string)stream.ReceiveNext());
        bool isActive = (bool)stream.ReceiveNext(); // IMPORTANT keep a local var, since the ?. may not call receive
        toolEffectReplicant?.SetActive(isActive);
        receivedToolTargetViewId = (int)stream.ReceiveNext();
        lastReceivedTargetPos = (Vector3)stream.ReceiveNext(); // IMPORTANT keep a local var, since the ?. may not call receive
        SetCreatePreviewSettingsJson((string)stream.ReceiveNext());
      }
      else
      {
        DestroyToolEffectReplicant();
        SetCreatePreviewSettingsJson(null);
        lerpedTargetPos = Vector3.positiveInfinity;
        lastReceivedTargetPos = Vector3.positiveInfinity;
      }
      // "Render enabled" field not present in earlier versions, defaults to true:
      SetPlayerVisible((bool)stream.ReceiveNext());
#if !USE_PUN
      animator.SetBool("IsCrouching", (bool)stream.ReceiveNext());
      SetGrounded((bool)stream.ReceiveNext());
      SetSprint((bool)stream.ReceiveNext());
      animator.SetBool("EditorHolograms", (bool)stream.ReceiveNext());
      animator.SetFloat("VelX", (float)stream.ReceiveNext());
      animator.SetFloat("VelY", (float)stream.ReceiveNext());

      int damageSequence = (int)stream.ReceiveNext();
      if (damageSequence != receivedDirectDamageSequence)
      {
        receivedDirectDamageSequence = damageSequence;
        animator.ResetTrigger("IsDamaged");
        animator.SetTrigger("IsDamaged");
      }
      int deathSequence = (int)stream.ReceiveNext();
      if (deathSequence != receivedDirectDeathSequence)
      {
        receivedDirectDeathSequence = deathSequence;
        animator.ResetTrigger("IsDead");
        animator.SetTrigger("IsDead");
      }
      int respawnSequence = (int)stream.ReceiveNext();
      if (respawnSequence != receivedDirectRespawnSequence)
      {
        receivedDirectRespawnSequence = respawnSequence;
        animator.ResetTrigger("IsRespawning");
        animator.SetTrigger("IsRespawning");
      }
#endif
    }
  }

  void DestroyToolEffectReplicant()
  {
    if (toolEffectReplicant != null)
    {
      GameObject.Destroy(toolEffectReplicant.GetGameObject());
      toolEffectReplicant = null;
    }
  }

  private void SetToolEffectName(string name)
  {
    Debug.Assert(name != null);

    if (currentToolEffectName != name)
    {
      // Need to change the instance.
      DestroyToolEffectReplicant();
      currentToolEffectName = name;

      Util.Log($"new tool effect name: {name}");

      foreach (var prefab in toolEffectPrefabs)
      {
        if (currentToolEffectName.Contains(prefab.name))
        {
          GameObject inst = GameObject.Instantiate(prefab, Vector3.zero, Quaternion.identity);
          toolEffectReplicant = inst.GetComponent<NetworkableToolEffect>();

          // YOLO. Assume it's an on-actor effect.
          if (toolEffectReplicant == null)
          {
            toolEffectReplicant = new OnActorToolEffect(inst);
          }

          toolEffectReplicant.SetSpatialAudio(true);
          toolEffectReplicant.SetRayOriginTransform(toolEmissionAnchor);
          toolEffectReplicant.SetTint(currentTint);
          break;
        }
      }

      // Note that it is OK if at this point, currentToolEffectInstance is null. That just means we don't support networking that effect.
    }
  }

  void LateUpdate()
  {
    if (!isLocalPlayer && toolEffectReplicant != null)
    {
      NetworkableToolEffect effect = toolEffectReplicant;
      if (receivedToolTargetViewId >= 0)
      {
        // Find the target actor, shoot a ray at it, and have the tool effect end there.
        PhotonView view = PhotonView.Find(receivedToolTargetViewId);
        VoosActor targetActor = view?.GetComponent<VoosActor>();
        effect.SetReceivedTargetActor(targetActor);
      }
      else
      {
        effect.SetReceivedTargetActor(null);
      }
      effect.OnLateUpdate();
    }
  }

  public void Setup(bool isLocalPlayer)
  {
    Debug.Assert(!setupCalled);
    setupCalled = true;
    this.isLocalPlayer = isLocalPlayer;
    if (!this.isLocalPlayer)
    {
      avatarAudioController.SetSpatial(true);
      MoveToDefaultLayer(transform);
      Util.SetLayerRecursively(playAvatar.gameObject, LayerMask.NameToLayer("VoosActor"));
    }
    if (inPlayMode)
    {
      avatarAudioController.LaunchAsExplorer();
    }
    else
    {
      avatarAudioController.LaunchAsEditor();
    }
  }
#if !USE_PUN
  public void SetDirectPose(Vector3 position, Quaternion rotation)
  {
    directTargetPosition = position;
    directTargetRotation = rotation;
    hasDirectTargetPose = true;
  }
#endif

  public bool GetIsLocalPlayer()
  {
    return this.isLocalPlayer;
  }

  void PlayerBody.EventHandler.OnTintChanged(Color tint)
  {
    SetTint(tint);
  }

  void MoveToDefaultLayer(Transform parentTransform)
  {
    parentTransform.gameObject.layer = LayerMask.NameToLayer("Default");
    foreach (Transform t in parentTransform)
    {
      if (t != parentTransform) MoveToDefaultLayer(t);
    }
  }

  void SetCreatePreviewSettingsJson(string createPreviewSettingsJson)
  {
    if (lastCreatePreviewSettingsJson == createPreviewSettingsJson) return;

    lastCreatePreviewSettingsJson = createPreviewSettingsJson;
    if (createPreviewSettingsJson.IsNullOrEmpty())
    {
      if (createPreviewInst != null)
      {
        GameObject.Destroy(createPreviewInst.gameObject);
        createPreviewInst = null;
      }
    }
    else
    {
      // Settings changed, create new preview
      var settings = JsonUtility.FromJson<CreatePreviewSettings>(createPreviewSettingsJson);

      if (createPreviewInst != null)
      {
        GameObject.Destroy(createPreviewInst.gameObject);
      }

      createPreviewInst = Instantiate(createPreviewPrefab);
      createPreviewInst.SetRenderableByReference(
        settings.renderable,
        settings.addlRotation,
        settings.renderableOffset,
        settings.renderableRotation,
        _ => settings.scale,
        () => true);

      createPreviewInst.SetTint(currentTint);
    }
  }

  void OnDestroy()
  {
    if (createPreviewInst != null)
    {
      GameObject.Destroy(createPreviewInst.gameObject);
    }

    if (toolEffectReplicant != null)
    {
      GameObject.Destroy(toolEffectReplicant.GetGameObject());
    }
    if (ownedTintMaterials != null)
    {
      foreach (Material material in ownedTintMaterials)
      {
        RuntimeModelResourceSet.DestroyResource(material);
      }
      ownedTintMaterials.Clear();
    }
    if (tintMaterialByRenderer != null) tintMaterialByRenderer.Clear();
  }
}
