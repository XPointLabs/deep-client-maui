(() => {
  "use strict";

  const stage = document.getElementById("stage");
  const localVideo = document.getElementById("localVideo");
  const remoteVideo = document.getElementById("remoteVideo");
  let peer = null;
  let localStream = null;
  let videoEnabled = false;
  let facingMode = "user";
  let mediaProbeTimer = null;
  const pendingCandidates = [];

  const send = message => {
    if (!window.HybridWebView || typeof window.HybridWebView.InvokeDotNet !== "function") return false;
    window.HybridWebView.InvokeDotNet("OnWebMessage", [JSON.stringify(message)]).catch(reportError);
    return true;
  };

  const reportError = error => {
    console.error("Deep call media operation failed");
    send({ type: "error", code: "media-operation-failed" });
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
    reportTrackState("audio");
    mediaProbeTimer = window.setInterval(() => reportMediaState().catch(reportError), 1000);

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
    if (!localStream) throw new Error("Local media is unavailable");
    let matched = false;
    for (const track of localStream.getTracks()) {
      if (track.kind === kind) {
        track.enabled = Boolean(enabled);
        matched = true;
      }
    }
    if (!matched) throw new Error("Requested local media track is unavailable");
    reportTrackState(kind);
  }

  function reportTrackState(kind) {
    const tracks = localStream ? localStream.getTracks().filter(track => track.kind === kind) : [];
    if (tracks.length !== 1) throw new Error("Local media track cardinality is invalid");
    send({ type: "control", control: kind, enabled: Boolean(tracks[0].enabled) });
  }

  async function reportMediaState() {
    if (!peer || peer.connectionState !== "connected") return;
    const stats = await peer.getStats();
    let inboundAudioPackets = 0;
    let outboundAudioPackets = 0;
    let selectedCandidatePair = false;
    let selectedCandidatePairId = null;

    stats.forEach(report => {
      if (report.type === "transport" && typeof report.selectedCandidatePairId === "string") {
        selectedCandidatePairId = report.selectedCandidatePairId;
      }
      if (report.type === "inbound-rtp" && !report.isRemote && (report.kind === "audio" || report.mediaType === "audio")) {
        inboundAudioPackets += Number(report.packetsReceived || 0);
      }
      if (report.type === "outbound-rtp" && !report.isRemote && (report.kind === "audio" || report.mediaType === "audio")) {
        outboundAudioPackets += Number(report.packetsSent || 0);
      }
    });

    stats.forEach(report => {
      if (report.type !== "candidate-pair" || report.state !== "succeeded") return;
      if ((selectedCandidatePairId && report.id === selectedCandidatePairId) || report.nominated === true || report.selected === true) {
        selectedCandidatePair = true;
      }
    });

    send({
      type: "media",
      iceSelected: selectedCandidatePair,
      inboundAudioActive: inboundAudioPackets > 0,
      outboundAudioActive: outboundAudioPackets > 0
    });
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
    if (mediaProbeTimer !== null) window.clearInterval(mediaProbeTimer);
    mediaProbeTimer = null;
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
