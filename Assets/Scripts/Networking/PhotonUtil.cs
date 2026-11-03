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

using UnityEngine;

public static class PhotonUtil
{
  public static bool ActuallyConnected()
  {
    return PhotonNetwork.connected && !PhotonNetwork.offlineMode;
  }

  public static void SendColor(this PhotonStream stream, Color color)
  {
#if USE_PUN
    Debug.Assert(stream.isWriting);
    stream.SendNext(color.r);
    stream.SendNext(color.g);
    stream.SendNext(color.b);
    stream.SendNext(color.a);
#else
    stream.SendNext(color);
#endif
  }

  public static Color ReceiveColor(this PhotonStream stream)
  {
#if USE_PUN
    Debug.Assert(stream.isReading);
    return new Color((float)stream.ReceiveNext(),
      (float)stream.ReceiveNext(),
      (float)stream.ReceiveNext(),
      (float)stream.ReceiveNext());
#else
    return (Color)stream.ReceiveNext();
#endif
  }

  public static string GetPhotonStateString()
  {
#if USE_PUN
    var state = new System.Text.StringBuilder();
    if (PhotonNetwork.connected) state.Append("C");
    if (PhotonNetwork.connectedAndReady) state.Append("D");
    if (PhotonNetwork.inRoom) state.Append("R");
    if (PhotonNetwork.insideLobby) state.Append("L");
    if (PhotonNetwork.isMasterClient) state.Append("M");
    if (PhotonNetwork.offlineMode) state.Append("O");
    return state.ToString();
#else
    return PhotonNetwork.offlineMode ? "O" : PhotonNetwork.isMasterClient ? "M" : "C";
#endif
  }

  public static void CheckNoOtherPlayers()
  {
    if (PhotonNetwork.playerList.Length > 1)
    {
      throw new System.Exception("This operation is invalid if any other players are in the same game/room.");
    }
  }

  public static void ForceResendToAllPlayers(this PhotonView reliableView)
  {
#if USE_PUN
    reliableView.lastOnSerializeDataSent = null;
#else
    DirectHostRpcRouter.ForceResend(reliableView);
#endif
  }
}
