(() => {
  "use strict";

  const stage = document.getElementById("stage");
  const localVideo = document.getElementById("localVideo");
  const remoteVideo = document.getElementById("remoteVideo");
  let peer = null;
  let localStream = null;
  let videoEnabled = false;
  let facingMode = "user";
  const pendingCandidates = [];

  const send = message => {
    if (!window.HybridWebView || typeof window.HybridWebView.InvokeDotNet !== "function") return false;
    window.HybridWebView.InvokeDotNet("OnWebMessage", [JSON.stringify(message)]).catch(reportError);
    return true;
  };

  const reportError = error => {
    const message = error && error.message ? error.message : String(error || "Unknown call error");
    send({ type: "error", message });
  };

  async function initialize(config) {
    if (peer) return;
    videoEnabled = Boolean(config.video);
    stage.classList.toggle("audio-only", !videoEnabled);
    peer = new RTCPeerConnection({
      iceServers: config.iceServers || [],
      bundlePolicy: "max-bundle",
      rtcpMuxPolicy: "require"
    });

    peer.onicecandidate = event => {
      if (event.candidate) {
        send({ type: "signal", signalType: "ice", payload: { candidate: event.candidate.toJSON() } });
      }
    };
    peer.ontrack = event => {
      remoteVideo.srcObject = event.streams[0];
      remoteVideo.play().catch(() => {});
    };
    peer.onconnectionstatechange = () => send({ type: "state", state: peer.connectionState });
    peer.oniceconnectionstatechange = () => {
      if (peer.iceConnectionState === "failed") peer.restartIce();
    };

    localStream = await navigator.mediaDevices.getUserMedia({
      audio: { echoCancellation: true, noiseSuppression: true, autoGainControl: true },
      video: videoEnabled ? { facingMode, width: { ideal: 1280 }, height: { ideal: 720 } } : false
    });
    localVideo.srcObject = localStream;
    for (const track of localStream.getTracks()) peer.addTrack(track, localStream);

    if (config.initiator) {
      const offer = await peer.createOffer();
      await peer.setLocalDescription(offer);
      send({
        type: "signal",
        signalType: "offer",
        payload: { description: peer.localDescription, video: videoEnabled }
      });
    }
  }

  async function acceptSignal(signalType, payload) {
    if (!peer) throw new Error("WebRTC is not initialized");
    if (signalType === "offer") {
      await peer.setRemoteDescription(payload.description);
      await flushCandidates();
      const answer = await peer.createAnswer();
      await peer.setLocalDescription(answer);
      send({ type: "signal", signalType: "answer", payload: { description: peer.localDescription } });
      return;
    }
    if (signalType === "answer") {
      await peer.setRemoteDescription(payload.description);
      await flushCandidates();
      return;
    }
    if (signalType === "ice" && payload.candidate) {
      if (peer.remoteDescription) await peer.addIceCandidate(payload.candidate);
      else pendingCandidates.push(payload.candidate);
    }
  }

  async function flushCandidates() {
    while (pendingCandidates.length > 0) {
      await peer.addIceCandidate(pendingCandidates.shift());
    }
  }

  function setTrackEnabled(kind, enabled) {
    if (!localStream) return;
    for (const track of localStream.getTracks()) {
      if (track.kind === kind) track.enabled = Boolean(enabled);
    }
  }

  async function switchCamera() {
    if (!peer || !localStream || !videoEnabled) return;
    facingMode = facingMode === "user" ? "environment" : "user";
    const replacement = await navigator.mediaDevices.getUserMedia({ video: { facingMode }, audio: false });
    const nextTrack = replacement.getVideoTracks()[0];
    const sender = peer.getSenders().find(value => value.track && value.track.kind === "video");
    if (sender) await sender.replaceTrack(nextTrack);
    for (const track of localStream.getVideoTracks()) track.stop();
    localStream.removeTrack(localStream.getVideoTracks()[0]);
    localStream.addTrack(nextTrack);
    localVideo.srcObject = localStream;
  }

  function hangup() {
    if (peer) peer.close();
    if (localStream) for (const track of localStream.getTracks()) track.stop();
    peer = null;
    localStream = null;
  }

  async function receive(message) {
    const parsed = typeof message === "string" ? JSON.parse(message) : message;
    switch (parsed.command) {
      case "initialize": await initialize(parsed); break;
      case "signal": await acceptSignal(parsed.signalType, parsed.payload); break;
      case "setMicrophone": setTrackEnabled("audio", parsed.enabled); break;
      case "setCamera": setTrackEnabled("video", parsed.enabled); break;
      case "switchCamera": await switchCamera(); break;
      case "hangup": hangup(); break;
    }
  }

  window.DeepCallReceive = message => receive(message).catch(reportError);

  window.addEventListener("HybridWebViewMessageReceived", event => {
    Promise.resolve().then(async () => {
      await receive(event.detail.message);
    }).catch(reportError);
  });

  window.addEventListener("beforeunload", hangup);
  function announceReady() {
    if (!send({ type: "ready" })) window.setTimeout(announceReady, 50);
  }

  if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", announceReady);
  else announceReady();
})();
