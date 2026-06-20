// ==UserScript==
// @name         YuraSub Bridge for asmr.one / Kikoeru
// @namespace    https://github.com/yurasub/yurasub
// @version      0.4.2
// @description  Push current browser audio subtitles to the local YuraSub desktop overlay.
// @author       Moeary
// @match        https://asmr.one/*
// @match        https://www.asmr.one/*
// @match        https://asmr-100.com/*
// @match        https://asmr-200.com/*
// @match        https://asmr-300.com/*
// @match        https://kikoeru.*.*/*
// @match        http://localhost:*/*
// @match        http://127.0.0.1:*/*
// @connect      127.0.0.1
// @connect      localhost
// @grant        GM_xmlhttpRequest
// ==/UserScript==

(function () {
  "use strict";

  const CONFIG = {
    endpoint: "ws://127.0.0.1:8765",
    httpEndpoint: "http://127.0.0.1:8766",
    pollIntervalMs: 120,
    httpCommandPollMs: 400,
    reconnectMinMs: 500,
    reconnectMaxMs: 5000,
    debug: false,
    hideSourceElement: false,
    clickThrough: false,
    splitMultilineTranslation: true,
    geometry: {
      width: 1100,
      height: 180,
      anchor: "bottom",
      marginBottom: 80,
    },
    style: {
      fontFamily: "Microsoft YaHei UI",
      fontSize: 36,
      translationFontSize: 24,
      textColor: "#ffffff",
      textOpacity: 100,
      translationColor: "#bfefff",
      translationOpacity: 100,
      outlineColor: "#101522",
      outlineWidth: 4,
      outlineOpacity: 100,
      shadowColor: "#000000a0",
      shadowOpacity: 65,
      backgroundColor: "#00000000",
      backgroundOpacity: 0,
      align: "center",
      maxLines: 4,
    },
    primarySelectors: [
      "#lyric",
      "[data-yurasub-lyric]",
      "[data-current-lyric]",
      "[data-current-subtitle]",
      ".current-lyric",
    ],
    selectors: [
      "#lyric",
      "[data-yurasub-lyric]",
      "[data-current-lyric]",
      "[data-current-subtitle]",
      ".lyric",
      ".lyrics",
      ".current-lyric",
      ".subtitle",
      ".subtitles",
      ".caption",
      ".vjs-text-track-display",
      "[class*='lyric' i]",
      "[class*='subtitle' i]",
      "[class*='caption' i]",
    ],
  };

  const state = {
    socket: null,
    reconnectTimer: 0,
    reconnectDelay: CONFIG.reconnectMinMs,
    lastKey: "",
    lastSourceElement: null,
    stickySourceElement: null,
    observer: null,
    httpLastCommandId: 0,
    httpPostAvailable: false,
  };

  const PREVIOUS_SELECTORS = [
    "button[aria-label*='上一']",
    "button[aria-label*='Previous' i]",
    "button[title*='上一']",
    "button[title*='Previous' i]",
    "[role='button'][aria-label*='上一']",
    "[role='button'][aria-label*='Previous' i]",
    "button[class*='prev' i]",
    "[role='button'][class*='prev' i]",
    "[data-testid*='prev' i]",
    "[data-plyr='previous']",
    "[aria-label*='前へ']",
    "[title*='前へ']",
    ".vjs-previous-control",
    ".aplayer-icon-back",
  ];

  const NEXT_SELECTORS = [
    "button[aria-label*='下一']",
    "button[aria-label*='Next' i]",
    "button[title*='下一']",
    "button[title*='Next' i]",
    "[role='button'][aria-label*='下一']",
    "[role='button'][aria-label*='Next' i]",
    "button[class*='next' i]",
    "[role='button'][class*='next' i]",
    "[data-testid*='next' i]",
    "[data-plyr='next']",
    "[aria-label*='次へ']",
    "[title*='次へ']",
    ".vjs-next-control",
    ".aplayer-icon-forward",
  ];

  const PLAY_PAUSE_SELECTORS = [
    "button[aria-label*='播放']",
    "button[aria-label*='暂停']",
    "button[aria-label*='Play' i]",
    "button[aria-label*='Pause' i]",
    "button[title*='播放']",
    "button[title*='暂停']",
    "button[title*='Play' i]",
    "button[title*='Pause' i]",
    "[role='button'][aria-label*='播放']",
    "[role='button'][aria-label*='暂停']",
    "[role='button'][aria-label*='Play' i]",
    "[role='button'][aria-label*='Pause' i]",
    "button[class*='play' i]",
    "button[class*='pause' i]",
    "[data-testid*='play' i]",
    "[data-testid*='pause' i]",
    "[data-plyr='play']",
    ".vjs-play-control",
    ".aplayer-button",
  ];

  function log(...args) {
    if (CONFIG.debug) {
      console.log("[YuraSub]", ...args);
    }
  }

  function connect() {
    if (state.socket && (state.socket.readyState === WebSocket.OPEN || state.socket.readyState === WebSocket.CONNECTING)) {
      return;
    }

    try {
      state.socket = new WebSocket(CONFIG.endpoint);
    } catch (error) {
      scheduleReconnect();
      return;
    }

    state.socket.addEventListener("open", () => {
      state.reconnectDelay = CONFIG.reconnectMinMs;
      send({
        type: "hello",
        page: location.href,
        title: document.title,
        clickThrough: CONFIG.clickThrough,
        geometry: CONFIG.geometry,
        style: CONFIG.style,
      });
      pushCurrentSubtitle(true);
      log("connected", CONFIG.endpoint);
    });

    state.socket.addEventListener("message", (event) => {
      handleServerMessage(event.data);
    });
    state.socket.addEventListener("close", scheduleReconnect);
    state.socket.addEventListener("error", scheduleReconnect);
  }

  function handleServerMessage(rawMessage) {
    let message = null;
    if (typeof rawMessage === "object" && rawMessage !== null) {
      message = rawMessage;
    } else {
      try {
        message = JSON.parse(String(rawMessage || ""));
      } catch (error) {
        return;
      }
    }
    if (!message || message.type !== "mediaCommand") {
      return;
    }
    const command = String(message.command || "");
    const media = document.querySelector("audio,video");
    if (command === "previousTrack") {
      if (
        clickMaterialIconButton("skip_previous") ||
        clickFirstVisible(PREVIOUS_SELECTORS) ||
        clickControlByHint(["上一", "previous", "prev", "back", "前へ", "skip_previous"])
      ) {
        scheduleSubtitleRefresh();
        return;
      }
      if (clickAdjacentPlaylistItem(-1)) {
        scheduleSubtitleRefresh();
        return;
      }
      if (media) {
        media.currentTime = 0;
      }
      dispatchMediaKey("MediaTrackPrevious");
      scheduleSubtitleRefresh();
    } else if (command === "nextTrack") {
      if (
        clickMaterialIconButton("skip_next") ||
        clickFirstVisible(NEXT_SELECTORS) ||
        clickControlByHint(["下一", "next", "forward", "次へ", "skip_next"])
      ) {
        scheduleSubtitleRefresh();
        return;
      }
      if (clickAdjacentPlaylistItem(1)) {
        scheduleSubtitleRefresh();
        return;
      }
      dispatchMediaKey("MediaTrackNext");
      scheduleSubtitleRefresh();
    } else if (command === "playPause") {
      if (clickMaterialIconButton(media && media.paused ? "play_arrow" : "pause") || clickFirstVisible(PLAY_PAUSE_SELECTORS)) {
        scheduleMediaPush();
        return;
      }
      if (!media) {
        dispatchMediaKey("MediaPlayPause");
        scheduleMediaPush();
        return;
      }
      if (media.paused) {
        media.play().catch(() => {});
      } else {
        media.pause();
      }
      scheduleMediaPush();
    } else if (command === "seekTo") {
      seekMediaTo(media, Number(message.time ?? message.position ?? message.seconds));
    } else if (command === "seekBackward" && media) {
      media.currentTime = Math.max(0, Number(media.currentTime || 0) - 5);
      scheduleMediaPush();
    } else if (command === "seekForward" && media) {
      const duration = Number(media.duration || 0);
      media.currentTime = duration > 0 ? Math.min(duration, Number(media.currentTime || 0) + 5) : Number(media.currentTime || 0) + 5;
      scheduleMediaPush();
    }
  }

  function clickFirstVisible(selectors) {
    for (const selector of selectors) {
      let elements = [];
      try {
        elements = Array.from(document.querySelectorAll(selector));
      } catch (error) {
        continue;
      }
      const element = elements.map(clickableElementFor).find((candidate) => candidate && isVisible(candidate) && !isDisabled(candidate));
      if (element) {
        element.click();
        return true;
      }
    }
    return false;
  }

  function clickMaterialIconButton(iconName) {
    const icons = Array.from(document.querySelectorAll(".material-icons, .q-icon"));
    for (const icon of icons) {
      if (normalizeText(icon.textContent) !== iconName) {
        continue;
      }
      const button = icon.closest?.("button, [role='button']");
      if (button && isVisible(button) && !isDisabled(button)) {
        button.click();
        return true;
      }
    }
    return false;
  }

  function clickControlByHint(hints) {
    const hintValues = hints.map((hint) => String(hint).toLowerCase());
    let candidates = [];
    try {
      candidates = Array.from(
        document.querySelectorAll("button, a, [role='button'], [class*='button' i], [class*='icon' i], [class*='btn' i]")
      );
    } catch (error) {
      candidates = Array.from(document.querySelectorAll("button, a, [role='button']"));
    }
    for (const candidate of candidates) {
      const element = clickableElementFor(candidate);
      if (!element || !isVisible(element) || isDisabled(element)) {
        continue;
      }
      const haystack = describeElementForControl(candidate).toLowerCase();
      if (hintValues.some((hint) => haystack.includes(hint))) {
        element.click();
        return true;
      }
    }
    return false;
  }

  function clickAdjacentPlaylistItem(direction) {
    const media = document.querySelector("audio,video");
    const items = collectPlaylistItems();
    if (items.length < 2) {
      return false;
    }
    const currentIndex = items.findIndex((item) => isCurrentPlaylistItem(item, media));
    if (currentIndex < 0) {
      return false;
    }
    const targetIndex = currentIndex + direction;
    if (targetIndex < 0 || targetIndex >= items.length) {
      return false;
    }
    const target = clickableElementFor(items[targetIndex]) || items[targetIndex];
    if (target && isVisible(target) && !isDisabled(target)) {
      target.click();
      return true;
    }
    return false;
  }

  function collectPlaylistItems() {
    const selectors = [
      ".aplayer-list li",
      "[class*='playlist' i] li",
      "[class*='play-list' i] li",
      "[class*='track-list' i] li",
      "[class*='audio-list' i] li",
      "[class*='file-list' i] li",
      "[class*='v-list-item' i]",
      "[role='listbox'] [role='option']",
      "li[class*='track' i]",
      "li[class*='audio' i]",
      "tr[class*='track' i]",
      "tr[class*='audio' i]",
    ];
    const seen = new Set();
    const items = [];
    for (const selector of selectors) {
      let elements = [];
      try {
        elements = Array.from(document.querySelectorAll(selector));
      } catch (error) {
        continue;
      }
      const dedicatedPlaylistSelector = /aplayer-list|playlist|play-list|track-list|audio-list|file-list/i.test(selector);
      for (const element of elements) {
        if (seen.has(element) || !isVisible(element) || (!dedicatedPlaylistSelector && !looksPlayableItem(element))) {
          continue;
        }
        seen.add(element);
        items.push(element);
      }
    }
    return items;
  }

  function looksPlayableItem(element) {
    const text = describeElementForControl(element);
    const identity = `${element.id || ""} ${element.className || ""}`;
    return (
      /\.(mp3|wav|flac|m4a|aac|ogg|opus|wma|webm)\b/i.test(text) ||
      /aplayer|audio|track|playing|current/i.test(identity) ||
      Boolean(element.querySelector?.("audio, button[class*='play' i], [role='button'][class*='play' i]"))
    );
  }

  function isCurrentPlaylistItem(element, media) {
    if (element.matches?.(".aplayer-list-light, .active, .current, .playing, [aria-current='true'], [data-active='true']")) {
      return true;
    }
    if (/active|current|playing|aplayer-list-light/i.test(`${element.id || ""} ${element.className || ""}`)) {
      return true;
    }
    if (element.getAttribute?.("aria-selected") === "true") {
      return true;
    }
    if (!media) {
      return false;
    }
    const sourceName = fileNameFromUrl(media.currentSrc || media.src || "");
    if (!sourceName) {
      return false;
    }
    return describeElementForControl(element).toLowerCase().includes(sourceName.toLowerCase());
  }

  function clickableElementFor(element) {
    if (!element) {
      return null;
    }
    if (element.matches?.("button, a, [role='button']")) {
      return element;
    }
    return element.closest?.("button, a, [role='button']") || element.querySelector?.("button, a, [role='button']") || element;
  }

  function isDisabled(element) {
    return Boolean(element?.disabled || element?.getAttribute?.("aria-disabled") === "true");
  }

  function describeElementForControl(element) {
    const values = [
      element?.textContent || "",
      element?.getAttribute?.("aria-label") || "",
      element?.getAttribute?.("title") || "",
      element?.getAttribute?.("data-testid") || "",
      element?.id || "",
      element?.className || "",
      element?.dataset?.src || "",
      element?.dataset?.url || "",
      element?.dataset?.path || "",
      element?.dataset?.file || "",
      element?.getAttribute?.("href") || "",
      element?.querySelector?.("a[href]")?.getAttribute("href") || "",
      element?.querySelector?.("use")?.getAttribute("href") || "",
      element?.querySelector?.("use")?.getAttribute("xlink:href") || "",
    ];
    return values.filter(Boolean).join(" ");
  }

  function fileNameFromUrl(url) {
    try {
      const parsed = new URL(url, location.href);
      const pathname = decodeURIComponent(parsed.pathname);
      return pathname.split("/").filter(Boolean).pop() || "";
    } catch (error) {
      return "";
    }
  }

  function seekMediaTo(media, seconds) {
    if (!media || !Number.isFinite(seconds)) {
      return false;
    }
    const duration = Number(media.duration || 0);
    const target = duration > 0 ? Math.min(duration, Math.max(0, seconds)) : Math.max(0, seconds);
    try {
      if (typeof media.fastSeek === "function") {
        media.fastSeek(target);
      } else {
        media.currentTime = target;
      }
      syncPlyrSeekInputs(target, duration);
      if (typeof Event === "function") {
        media.dispatchEvent(new Event("seeking"));
        media.dispatchEvent(new Event("timeupdate"));
        media.dispatchEvent(new Event("seeked"));
      }
      scheduleMediaPush(50);
      return true;
    } catch (error) {
      return false;
    }
  }

  function syncPlyrSeekInputs(seconds, duration) {
    if (!duration || duration <= 0) {
      return;
    }
    const percent = Math.max(0, Math.min(100, (seconds / duration) * 100));
    const inputs = Array.from(document.querySelectorAll("input[data-plyr='seek'], input[type='range'][aria-label='Seek']"));
    for (const input of inputs) {
      input.value = String(percent);
      input.setAttribute("aria-valuenow", String(seconds));
      input.style.setProperty("--value", `${percent}%`);
      input.dispatchEvent(new Event("input", { bubbles: true }));
      input.dispatchEvent(new Event("change", { bubbles: true }));
    }
  }

  function scheduleMediaPush(delay = 250) {
    window.setTimeout(() => pushMediaState(true), delay);
  }

  function scheduleSubtitleRefresh(delay = 350) {
    window.setTimeout(() => {
      pushMediaState(true);
      pushCurrentSubtitle(true);
    }, delay);
  }

  function dispatchMediaKey(key) {
    try {
      document.dispatchEvent(new KeyboardEvent("keydown", { key, bubbles: true, cancelable: true }));
      document.dispatchEvent(new KeyboardEvent("keyup", { key, bubbles: true, cancelable: true }));
      return true;
    } catch (error) {
      return false;
    }
  }

  function scheduleReconnect() {
    if (state.reconnectTimer) {
      return;
    }
    state.reconnectTimer = window.setTimeout(() => {
      state.reconnectTimer = 0;
      state.reconnectDelay = Math.min(state.reconnectDelay * 1.5, CONFIG.reconnectMaxMs);
      connect();
    }, state.reconnectDelay);
  }

  function send(payload) {
    if (!state.socket || state.socket.readyState !== WebSocket.OPEN) {
      return sendHttp(payload);
    }
    state.socket.send(JSON.stringify(payload));
    return true;
  }

  function sendHttp(payload) {
    const body = JSON.stringify(payload);
    if (typeof GM_xmlhttpRequest === "function") {
      GM_xmlhttpRequest({
        method: "POST",
        url: `${CONFIG.httpEndpoint}/subtitle`,
        headers: { "Content-Type": "application/json" },
        data: body,
        timeout: 2500,
        onload: () => {
          state.httpPostAvailable = true;
        },
        onerror: () => {
          state.httpPostAvailable = false;
        },
        ontimeout: () => {
          state.httpPostAvailable = false;
        },
      });
      return true;
    }
    fetch(`${CONFIG.httpEndpoint}/subtitle`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body,
      mode: "cors",
      keepalive: true,
    })
      .then(() => {
        state.httpPostAvailable = true;
      })
      .catch(() => {
        state.httpPostAvailable = false;
      });
    return true;
  }

  function pollHttpCommands() {
    if (state.socket && state.socket.readyState === WebSocket.OPEN) {
      return;
    }
    const url = `${CONFIG.httpEndpoint}/commands?since=${state.httpLastCommandId}`;
    const handleCommands = (payload) => {
      if (!payload || !Array.isArray(payload.commands)) {
        return;
      }
      for (const command of payload.commands) {
        state.httpLastCommandId = Math.max(state.httpLastCommandId, Number(command.id || 0));
        handleServerMessage(command);
      }
    };
    if (typeof GM_xmlhttpRequest === "function") {
      GM_xmlhttpRequest({
        method: "GET",
        url,
        timeout: 2500,
        onload: (response) => {
          state.httpPostAvailable = true;
          try {
            handleCommands(JSON.parse(response.responseText || "{}"));
          } catch (error) {
            log("invalid http command response", error);
          }
        },
        onerror: () => {
          state.httpPostAvailable = false;
        },
        ontimeout: () => {
          state.httpPostAvailable = false;
        },
      });
      return;
    }
    fetch(url, { method: "GET", mode: "cors", cache: "no-store" })
      .then((response) => response.json())
      .then((payload) => {
        state.httpPostAvailable = true;
        handleCommands(payload);
      })
      .catch(() => {
        state.httpPostAvailable = false;
      });
  }

  function pushCurrentSubtitle(force = false) {
    const current = readCurrentSubtitle();
    const text = normalizeText(current.text);
    const translation = normalizeText(current.translation);
    const media = readMediaState();
    const key = `${text}\n---\n${translation}\n---\n${current.source || ""}`;

    if (!force && key === state.lastKey) {
      return;
    }
    state.lastKey = key;

    if (!text && !translation) {
      send({ type: "clear", page: location.href, title: document.title, source: current.source });
      return;
    }

    send({
      type: "subtitle",
      text,
      translation,
      source: current.source,
      page: location.href,
      title: document.title,
      media,
      clickThrough: CONFIG.clickThrough,
      geometry: CONFIG.geometry,
      style: CONFIG.style,
      ts: Date.now(),
    });
  }

  function pushMediaState(force = false) {
    const media = readMediaState();
    if (!media) {
      return;
    }
    const key = mediaStateKey(media);
    if (!force && key === state.lastMediaKey) {
      return;
    }
    state.lastMediaKey = key;
    send({
      type: "media",
      media,
      page: location.href,
      title: document.title,
      ts: Date.now(),
    });
  }

  function mediaStateKey(media) {
    const currentTime = Number(media.currentTime || 0);
    const duration = Number(media.duration || 0);
    return [
      Math.floor((Number.isFinite(currentTime) ? currentTime : 0) * 4),
      Math.floor(Number.isFinite(duration) ? duration : 0),
      media.paused ? 1 : 0,
      media.src || "",
    ].join("|");
  }

  function readCurrentSubtitle() {
    const trackCue = readActiveTextTrackCue();
    if (trackCue.text) {
      return trackCue;
    }

    const primary = readPrimarySubtitleElement();
    if (primary) {
      updateSourceObserver(primary.element);
      state.stickySourceElement = primary.element;
      return primary;
    }

    if (state.stickySourceElement && document.contains(state.stickySourceElement) && isVisible(state.stickySourceElement)) {
      const parts = splitSubtitleText(state.stickySourceElement.innerText || state.stickySourceElement.textContent || "");
      return {
        element: state.stickySourceElement,
        text: parts.text,
        translation: readTranslation(state.stickySourceElement) || parts.translation,
        source: `${selectorFor(state.stickySourceElement, "sticky")}:sticky`,
      };
    }

    const candidates = collectDomCandidates();
    if (candidates.length === 0) {
      updateSourceObserver(null);
      return { text: "", translation: "", source: "none" };
    }

    candidates.sort((a, b) => b.score - a.score);
    const best = candidates[0];
    updateSourceObserver(best.element);
    if (best.score >= 200) {
      state.stickySourceElement = best.element;
    }

    if (CONFIG.hideSourceElement && best.element) {
      best.element.style.opacity = "0";
    }

    return {
      text: best.text,
      translation: best.translation || "",
      source: best.source,
    };
  }

  function readPrimarySubtitleElement() {
    for (const selector of CONFIG.primarySelectors) {
      let elements = [];
      try {
        elements = Array.from(document.querySelectorAll(selector));
      } catch (error) {
        continue;
      }
      for (const element of elements) {
        if (!isVisible(element)) {
          continue;
        }
        const parts = splitSubtitleText(element.innerText || element.textContent || "");
        return {
          element,
          text: parts.text,
          translation: readTranslation(element) || parts.translation,
          source: `${selectorFor(element, selector)}:primary`,
        };
      }
    }
    return null;
  }

  function readActiveTextTrackCue() {
    const media = document.querySelector("audio,video");
    if (!media || !media.textTracks) {
      return { text: "", translation: "", source: "textTrack:none" };
    }

    for (const track of media.textTracks) {
      const cues = track.activeCues || [];
      if (track.mode === "disabled") {
        track.mode = "hidden";
      }
      if (cues.length > 0) {
        const parts = splitSubtitleText(Array.from(cues).map((cue) => cue.text || "").filter(Boolean).join("\n"));
        if (parts.text || parts.translation) {
          return { text: parts.text, translation: parts.translation, source: `textTrack:${track.label || track.language || "default"}` };
        }
      }
    }
    return { text: "", translation: "", source: "textTrack:none" };
  }

  function collectDomCandidates() {
    const seen = new Set();
    const candidates = [];
    for (const selector of CONFIG.selectors) {
      let elements = [];
      try {
        elements = Array.from(document.querySelectorAll(selector));
      } catch (error) {
        continue;
      }
      for (const element of elements) {
        if (seen.has(element) || !isVisible(element)) {
          continue;
        }
        seen.add(element);
        const fullText = normalizeText(element.innerText || element.textContent || "");
        if (!isLikelySubtitleText(fullText)) {
          continue;
        }
        const parts = splitSubtitleText(fullText);
        candidates.push({
          element,
          text: parts.text,
          translation: readTranslation(element) || parts.translation,
          source: selectorFor(element, selector),
          score: scoreCandidate(element, selector, fullText),
        });
      }
    }
    return candidates;
  }

  function updateSourceObserver(element) {
    if (state.lastSourceElement === element) {
      return;
    }
    if (state.observer) {
      state.observer.disconnect();
      state.observer = null;
    }
    state.lastSourceElement = element;
    if (!element) {
      return;
    }
    state.observer = new MutationObserver(() => pushCurrentSubtitle(false));
    state.observer.observe(element, { childList: true, subtree: true, characterData: true, attributes: true });
    log("source", selectorFor(element, "observed"), normalizeText(element.innerText || element.textContent || ""));
  }

  function readTranslation(element) {
    const explicit = element?.dataset?.translation || element?.dataset?.translated || "";
    if (explicit) {
      return normalizeText(explicit);
    }
    const translationNode = element?.querySelector?.(
      "[data-yurasub-translation], [data-translation], .translation, .translated, .subtitle-translation, [class*='translation' i]"
    );
    return translationNode ? normalizeText(translationNode.innerText || translationNode.textContent || "") : "";
  }

  function scoreCandidate(element, selector, text) {
    let score = 0;
    if (selector === "#lyric" || element.id === "lyric") {
      score += 1000;
    }
    if (/current|active|playing|lyric|subtitle|caption/i.test(`${element.id} ${element.className}`)) {
      score += 80;
    }
    const rect = element.getBoundingClientRect();
    if (rect.width > 80 && rect.height > 12) {
      score += 20;
    }
    if (text.length <= 160) {
      score += 20;
    }
    if (element.matches("[aria-hidden='true'], [hidden]")) {
      score -= 200;
    }
    return score;
  }

  function isVisible(element) {
    const style = window.getComputedStyle(element);
    if (style.display === "none" || style.visibility === "hidden" || Number(style.opacity) === 0) {
      return false;
    }
    const rect = element.getBoundingClientRect();
    return rect.width > 0 && rect.height > 0;
  }

  function isLikelySubtitleText(text) {
    if (!text || text.length > 320) {
      return false;
    }
    const lines = text.split("\n").filter(Boolean);
    if (lines.length > 6) {
      return false;
    }
    if (/^(library|settings|login|logout|search|sort|filter)$/i.test(text)) {
      return false;
    }
    if (/^[A-Z0-9_-]{2,8}$/.test(text) && !/[\p{Script=Han}\p{Script=Hiragana}\p{Script=Katakana}]/u.test(text)) {
      return false;
    }
    return /[\p{Script=Han}\p{Script=Hiragana}\p{Script=Katakana}\p{Letter}\p{Number}]/u.test(text);
  }

  function splitSubtitleText(text) {
    const lines = normalizeLines(text);
    if (!CONFIG.splitMultilineTranslation || lines.length <= 1) {
      return { text: lines.join("\n"), translation: "" };
    }
    return { text: lines[0], translation: lines.slice(1).join("\n") };
  }

  function normalizeText(text) {
    return normalizeLines(text).join("\n").trim();
  }

  function normalizeLines(text) {
    return String(text || "")
      .replace(/\r\n?/g, "\n")
      .split("\n")
      .map((line) => line.replace(/\s+/g, " ").trim())
      .filter(Boolean);
  }

  function readMediaState() {
    const media = document.querySelector("audio,video");
    if (!media) {
      return null;
    }
    return {
      currentTime: Number(media.currentTime || 0),
      duration: Number(media.duration || 0),
      paused: Boolean(media.paused),
      playbackRate: Number(media.playbackRate || 1),
      src: media.currentSrc || media.src || "",
    };
  }

  function selectorFor(element, matchedSelector) {
    if (!element) {
      return matchedSelector || "unknown";
    }
    if (element.id) {
      return `#${element.id}`;
    }
    const className = String(element.className || "").trim().split(/\s+/).filter(Boolean).slice(0, 3).join(".");
    return className ? `${element.tagName.toLowerCase()}.${className}` : element.tagName.toLowerCase();
  }

  window.YuraSubDebug = {
    config: CONFIG,
    reconnect: connect,
    push: () => pushCurrentSubtitle(true),
    pushMedia: () => pushMediaState(true),
    status: () => ({
      endpoint: CONFIG.endpoint,
      socketState: state.socket ? state.socket.readyState : null,
      socketStateText: state.socket ? ["CONNECTING", "OPEN", "CLOSING", "CLOSED"][state.socket.readyState] : "NONE",
      httpEndpoint: CONFIG.httpEndpoint,
      httpPostAvailable: state.httpPostAvailable,
      httpLastCommandId: state.httpLastCommandId,
      lastKey: state.lastKey,
      lastMediaKey: state.lastMediaKey,
      source: selectorFor(state.lastSourceElement, "none"),
      stickySource: selectorFor(state.stickySourceElement, "none"),
    }),
    current: () => {
      const current = readCurrentSubtitle();
      return { text: current.text, translation: current.translation || "", source: current.source };
    },
    inspect: () => collectDomCandidates().map((candidate) => ({
      source: candidate.source,
      score: candidate.score,
      text: candidate.text,
      translation: candidate.translation || "",
      rect: candidate.element.getBoundingClientRect().toJSON(),
      id: candidate.element.id,
      className: String(candidate.element.className || ""),
    })),
  };

  connect();
  window.setInterval(() => {
    connect();
    pushCurrentSubtitle(false);
    pushMediaState(false);
  }, CONFIG.pollIntervalMs);
  window.setInterval(pollHttpCommands, CONFIG.httpCommandPollMs);
})();
