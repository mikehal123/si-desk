(function () {
  "use strict";

  var TOKEN_KEY = "sovereignPhoneToken";
  var CURSOR_KEY = "sovereignPhoneNotifCursor";
  var NOTIFY_ASKED_KEY = "sovereignPhoneNotifyAsked";

  var state = {
    epoch: 0,
    token: "",
    pairCode: "",
    view: "boot",
    session: { notify: false, name: "" },
    chats: [],
    chatSig: "",
    approvals: [],
    approvalSig: "",
    confirmIds: {},
    decisionId: "",
    focusConfirmId: "",
    openChat: null,
    awaitingWork: false,
    idlePolls: 0,
    notifCursor: null,
    notifPrimed: false,
    approvalError: "",
    chatTimer: 0,
    approvalTimer: 0,
    notifTimer: 0,
    chatFlight: false,
    approvalFlight: false,
    notifFlight: false,
    sending: false,
    stopping: false,
    pairing: false
  };

  var ui = {};

  function asString(value) {
    return typeof value === "string" ? value : "";
  }

  function asText(value) {
    if (typeof value === "string") return value;
    if (value == null) return "";
    if (typeof value === "number" || typeof value === "boolean") return String(value);
    try { return JSON.stringify(value, null, 2); } catch (err) { return ""; }
  }

  function asBool(value) {
    return value === true;
  }

  function clear(node) {
    while (node.firstChild) node.removeChild(node.firstChild);
  }

  function el(tag, className, text) {
    var node = document.createElement(tag);
    if (className) node.className = className;
    if (text != null) node.textContent = String(text);
    node.dir = "auto";
    return node;
  }

  function button(label, className, onClick) {
    var node = document.createElement("button");
    node.type = "button";
    node.className = className || "btn";
    node.textContent = label;
    node.addEventListener("click", onClick);
    return node;
  }

  function readToken() {
    try {
      var value = sessionStorage.getItem(TOKEN_KEY);
      return typeof value === "string" ? value : "";
    } catch (err) {
      return "";
    }
  }

  function writeToken(token) {
    sessionStorage.setItem(TOKEN_KEY, token);
    state.token = token;
  }

  function pairingCodeFromHash() {
    var raw = location.hash.charAt(0) === "#" ? location.hash.slice(1) : location.hash;
    try { raw = decodeURIComponent(raw); } catch (err) { /* Hex codes are not escaped. */ }
    raw = raw.trim();
    if (/^[0-9a-fA-F]{16,128}$/.test(raw)) return raw;
    return "";
  }

  function deviceName(value) {
    var cleaned = String(value || "").replace(/[\u0000-\u001F\u007F]/g, "").trim().slice(0, 40);
    return cleaned || "My phone";
  }

  function redact(text) {
    return String(text)
      .replace(/Bearer\s+\S+/gi, "Bearer [redacted]")
      .replace(/\b[0-9a-fA-F]{32,}\b/g, "[redacted]");
  }

  function errorText(result, fallback) {
    if (result && result.data && typeof result.data.error === "string" && result.data.error) return redact(result.data.error);
    if (result && result.status === 429) return "Too many attempts. Wait and try again.";
    if (result && result.status === 0) return "Can't reach xAI Desk.";
    return fallback;
  }

  function formatUpdated(value) {
    if (typeof value !== "string" || !value) return "";
    var date = new Date(value);
    if (isNaN(date.getTime())) return value;
    return date.toLocaleString();
  }

  function api(method, path, body, useAuth) {
    var ticket = state.epoch;
    var headers = { Accept: "application/json" };
    if (body !== undefined) headers["Content-Type"] = "application/json";
    if (useAuth !== false && state.token) headers.Authorization = "Bearer " + state.token;
    return fetch(path, {
      method: method,
      headers: headers,
      body: body === undefined ? undefined : JSON.stringify(body),
      cache: "no-store"
    }).then(function (response) {
      return response.text().then(function (text) {
        var data = {};
        if (text) {
          try { data = JSON.parse(text); } catch (err) { data = {}; }
        }
        var result = { ok: response.ok, status: response.status, data: data, stale: ticket !== state.epoch };
        if (useAuth !== false && response.status === 401 && ticket === state.epoch) {
          forgetToken();
          renderScan("This phone is no longer paired. Scan the QR code in Settings on the desktop.");
          result.stale = true;
        }
        return result;
      });
    }).catch(function () {
      return { ok: false, status: 0, data: {}, stale: ticket !== state.epoch };
    });
  }

  function forgetToken() {
    state.epoch += 1;
    state.token = "";
    state.openChat = null;
    state.chats = [];
    state.approvals = [];
    state.notifCursor = null;
    state.notifPrimed = false;
    stopChatPoll();
    stopGlobalPolls();
    try {
      sessionStorage.removeItem(TOKEN_KEY);
      sessionStorage.removeItem(CURSOR_KEY);
    } catch (err) { /* Storage may be blocked. */ }
  }

  function stopChatPoll() {
    if (state.chatTimer) clearInterval(state.chatTimer);
    state.chatTimer = 0;
    state.awaitingWork = false;
    state.idlePolls = 0;
  }

  function stopGlobalPolls() {
    if (state.approvalTimer) clearInterval(state.approvalTimer);
    if (state.notifTimer) clearInterval(state.notifTimer);
    state.approvalTimer = 0;
    state.notifTimer = 0;
  }

  function mount() {
    var root = document.getElementById("app");
    clear(root);
    ui.header = document.createElement("header");
    ui.banner = document.createElement("div");
    ui.banner.id = "banner";
    ui.banner.setAttribute("role", "status");
    ui.banner.setAttribute("aria-live", "polite");
    ui.banner.hidden = true;
    ui.main = document.createElement("main");
    ui.main.id = "main";
    ui.nav = document.createElement("nav");
    ui.nav.setAttribute("aria-label", "Primary");
    var chats = button("Chats", "btn secondary", function () { showChats(); });
    chats.id = "tab-chats";
    var approvals = button("Approvals", "btn secondary", function () { showApprovals(); });
    approvals.id = "tab-approvals";
    ui.nav.appendChild(chats);
    ui.nav.appendChild(approvals);
    root.appendChild(ui.header);
    root.appendChild(ui.banner);
    root.appendChild(ui.main);
    root.appendChild(ui.nav);
  }

  function hideBanner() {
    ui.banner.hidden = true;
    clear(ui.banner);
  }

  function showBanner(messages) {
    var list = Array.isArray(messages) ? messages : [messages];
    clear(ui.banner);
    list.forEach(function (message) {
      if (!message) return;
      ui.banner.appendChild(el("p", "", message));
    });
    if (!ui.banner.firstChild) return;
    ui.banner.appendChild(button("Dismiss", "btn secondary", hideBanner));
    ui.banner.hidden = false;
  }

  function setHeader(nodes) {
    clear(ui.header);
    nodes.forEach(function (node) { if (node) ui.header.appendChild(node); });
  }

  function syncTabs() {
    var authed = state.view === "chats" || state.view === "chat" || state.view === "approvals";
    ui.nav.hidden = !authed;
    var chats = document.getElementById("tab-chats");
    var approvals = document.getElementById("tab-approvals");
    if (chats) {
      if (state.view === "chats") chats.setAttribute("aria-current", "page");
      else chats.removeAttribute("aria-current");
    }
    if (approvals) {
      var count = state.approvals.length;
      approvals.textContent = count ? "Approvals (" + count + ")" : "Approvals";
      if (state.view === "approvals") approvals.setAttribute("aria-current", "page");
      else approvals.removeAttribute("aria-current");
    }
  }

  function renderScan(message) {
    state.view = "scan";
    hideBanner();
    setHeader([el("h1", "", "xAI Desk")]);
    clear(ui.main);
    var gate = el("section", "gate");
    gate.appendChild(el("h2", "", "Pair this phone"));
    gate.appendChild(el("p", "", message || "Scan the QR code in Settings on the desktop."));
    ui.main.appendChild(gate);
    syncTabs();
  }

  function renderOffline(message) {
    state.view = "offline";
    hideBanner();
    setHeader([el("h1", "", "xAI Desk")]);
    clear(ui.main);
    var gate = el("section", "gate");
    gate.appendChild(el("p", "alert", message || "Can't reach xAI Desk."));
    gate.setAttribute("role", "alert");
    gate.appendChild(button("Retry", "btn", function () { enterApp(); }));
    ui.main.appendChild(gate);
    syncTabs();
  }

  function renderPair() {
    state.view = "pair";
    stopChatPoll();
    stopGlobalPolls();
    hideBanner();
    setHeader([el("h1", "", "xAI Desk")]);
    clear(ui.main);
    var gate = el("section", "gate");
    var form = document.createElement("form");
    form.className = "stack";
    form.noValidate = true;
    var field = el("div", "field");
    var label = el("label", "", "Phone name");
    label.htmlFor = "device-name";
    var input = document.createElement("input");
    input.id = "device-name";
    input.name = "name";
    input.type = "text";
    input.value = "My phone";
    input.maxLength = 40;
    input.autocomplete = "off";
    input.required = true;
    field.appendChild(label);
    field.appendChild(input);
    form.appendChild(field);
    form.appendChild(el("p", "muted", "Pairing code ready."));
    var submit = document.createElement("button");
    submit.type = "submit";
    submit.className = "btn";
    submit.textContent = "Pair";
    form.appendChild(submit);
    var alert = el("p", "alert");
    alert.id = "pair-error";
    alert.hidden = true;
    alert.setAttribute("role", "alert");
    form.appendChild(alert);
    form.addEventListener("submit", submitPair);
    gate.appendChild(form);
    if (state.token) gate.appendChild(button("Cancel", "btn secondary", function () { enterApp(); }));
    ui.main.appendChild(gate);
    syncTabs();
    input.focus();
  }

  function submitPair(event) {
    event.preventDefault();
    if (state.pairing) return;
    state.pairing = true;
    var input = document.getElementById("device-name");
    var submit = event.currentTarget.querySelector("button[type=submit]");
    var alert = document.getElementById("pair-error");
    var name = deviceName(input ? input.value : "");
    var code = state.pairCode;
    if (input) input.value = name;
    if (submit) submit.disabled = true;
    if (alert) alert.hidden = true;
    api("POST", "/api/pair", { code: code, name: name }, false).then(function (result) {
      state.pairing = false;
      if (submit) submit.disabled = false;
      if (!result || result.stale) return;
      var token = result.data && typeof result.data.token === "string" ? result.data.token : "";
      if (!result.ok || !token) {
        if (alert) {
          alert.textContent = errorText(result, "The pairing code was not accepted.");
          alert.hidden = false;
        }
        return;
      }
      try {
        writeToken(token);
        sessionStorage.removeItem(CURSOR_KEY);
      } catch (err) {
        if (alert) {
          alert.textContent = "This browser blocked session storage, so the phone cannot stay paired.";
          alert.hidden = false;
        }
        return;
      }
      state.pairCode = "";
      state.notifCursor = null;
      state.notifPrimed = false;
      try { history.replaceState(null, "", location.pathname + location.search); } catch (err) { /* Hash is already unused. */ }
      enterApp();
    });
  }

  function enterApp() {
    var ticket = state.epoch;
    api("GET", "/api/session").then(function (result) {
      if (ticket !== state.epoch || !result || result.stale) return;
      if (!result.ok) {
        renderOffline(errorText(result, "Can't reach xAI Desk."));
        return;
      }
      state.session = {
        notify: result.data && result.data.notify === true,
        name: result.data ? asString(result.data.name) : ""
      };
      if (state.session.notify) enableNotifications();
      startGlobalPolls();
      showChats();
    });
  }

  function enableNotifications() {
    if (!("serviceWorker" in navigator)) return;
    navigator.serviceWorker.register("/sw.js").catch(function () {});
    askNotificationPermission();
    if (!state.notifyGesture) {
      state.notifyGesture = true;
      document.addEventListener("pointerdown", function once() {
        askNotificationPermission();
      }, { once: true });
    }
  }

  function askNotificationPermission() {
    if (!("Notification" in window)) return;
    if (Notification.permission !== "default") return;
    try {
      if (sessionStorage.getItem(NOTIFY_ASKED_KEY) === "1") return;
    } catch (err) { return; }
    Notification.requestPermission().then(function (permission) {
      if (permission === "granted" || permission === "denied") {
        try { sessionStorage.setItem(NOTIFY_ASKED_KEY, "1"); } catch (err) { /* Ignore. */ }
      }
    }).catch(function () {});
  }

  function startGlobalPolls() {
    stopGlobalPolls();
    var ticket = state.epoch;
    refreshApprovals();
    pollNotifications();
    state.approvalTimer = setInterval(function () {
      if (ticket === state.epoch) refreshApprovals();
    }, 2000);
    state.notifTimer = setInterval(function () {
      if (ticket === state.epoch) pollNotifications();
    }, 5000);
  }

  function showChats() {
    stopChatPoll();
    state.view = "chats";
    state.openChat = null;
    hideBanner();
    var title = el("h1", "", "xAI Desk");
    var name = state.session.name ? el("p", "device-name", state.session.name) : null;
    setHeader(name ? [title, name] : [title]);
    if (state.chats.length) renderChatList();
    else {
      clear(ui.main);
      ui.main.appendChild(el("p", "empty", "Loading chats…"));
    }
    syncTabs();
    loadChats();
  }

  function loadChats() {
    var ticket = state.epoch;
    api("GET", "/api/chats").then(function (result) {
      if (ticket !== state.epoch || !result || result.stale || state.view !== "chats") return;
      if (!result.ok) {
        state.chats = [];
        state.chatSig = "";
        renderChatList(errorText(result, "Couldn't load chats."));
        return;
      }
      var chats = normalizeChats(result.data && result.data.chats);
      var sig = chats.map(chatSig).join("\n");
      state.chats = chats;
      if (sig === state.chatSig && document.getElementById("chat-list")) return;
      state.chatSig = sig;
      renderChatList();
    });
  }

  function chatSig(chat) {
    return [chat.id, chat.title, chat.provider, chat.model, chat.working, chat.updated, chat.projectId].join("\u0001");
  }

  function normalizeChats(list) {
    if (!Array.isArray(list)) return [];
    return list.map(function (item) {
      return {
        id: asString(item && item.id),
        title: asString(item && item.title),
        provider: asString(item && item.provider),
        model: asString(item && item.model),
        working: asBool(item && item.working),
        updated: asString(item && item.updated),
        projectId: asString(item && item.projectId)
      };
    }).filter(function (chat) { return chat.id; });
  }

  function renderChatList(error) {
    clear(ui.main);
    if (error) {
      var problem = el("p", "alert empty", error);
      problem.setAttribute("role", "alert");
      ui.main.appendChild(problem);
      ui.main.appendChild(button("Retry", "btn", loadChats));
      return;
    }
    if (!state.chats.length) {
      ui.main.appendChild(el("p", "empty", "No chats yet."));
      return;
    }
    var list = document.createElement("ul");
    list.id = "chat-list";
    list.className = "list";
    state.chats.forEach(function (chat) {
      var item = document.createElement("li");
      var row = button("", "chat-row", function () { openChat(chat.id); });
      var title = chat.title || "Untitled chat";
      row.appendChild(el("span", "row-title", title));
      var bits = [];
      if (chat.provider) bits.push(chat.provider);
      if (chat.model) bits.push(chat.model);
      var when = formatUpdated(chat.updated);
      if (when) bits.push(when);
      if (chat.projectId) bits.push(chat.projectId);
      if (bits.length) row.appendChild(el("span", "row-meta", bits.join(" · ")));
      row.appendChild(el("span", chat.working ? "row-meta working" : "row-meta", chat.working ? "Working" : "Idle"));
      row.setAttribute("aria-label", (chat.working ? "Working chat " : "Open chat ") + title);
      item.appendChild(row);
      list.appendChild(item);
    });
    ui.main.appendChild(list);
  }

  function openChat(id) {
    stopChatPoll();
    state.view = "chat";
    state.openChat = {
      id: id,
      title: "",
      provider: "",
      model: "",
      status: "",
      working: false,
      projectId: "",
      liveReply: "",
      activityPhase: "",
      turns: [],
      subagents: []
    };
    state.chatSig = "";
    hideBanner();
    renderChatShell();
    syncTabs();
    refreshOpenChat();
  }

  function renderChatShell() {
    var chat = state.openChat;
    var row = el("div", "header-row");
    var back = button("Back", "btn secondary", function () { showChats(); });
    back.setAttribute("aria-label", "Back to chats");
    row.appendChild(back);
    var title = el("h1", "", chat.title || "Untitled chat");
    title.id = "chat-title";
    row.appendChild(title);
    setHeader([row]);
    clear(ui.main);
    var meta = document.createElement("div");
    meta.id = "chat-meta";
    var transcript = document.createElement("div");
    transcript.id = "transcript";
    transcript.setAttribute("aria-label", "Conversation");
    var form = document.createElement("form");
    form.className = "composer";
    form.noValidate = true;
    var label = el("label", "sr-only", "Message");
    label.htmlFor = "message";
    var box = document.createElement("textarea");
    box.id = "message";
    box.name = "text";
    box.rows = 3;
    box.placeholder = "Message";
    box.setAttribute("enterkeyhint", "send");
    var actions = el("div", "composer-actions");
    var stop = button("Stop", "btn danger", stopChat);
    stop.id = "stop";
    stop.hidden = !chat.working;
    var send = document.createElement("button");
    send.type = "submit";
    send.className = "btn";
    send.id = "send";
    send.textContent = "Send";
    actions.appendChild(stop);
    actions.appendChild(send);
    var alert = el("p", "alert");
    alert.id = "composer-error";
    alert.hidden = true;
    alert.setAttribute("role", "alert");
    form.appendChild(label);
    form.appendChild(box);
    form.appendChild(actions);
    form.appendChild(alert);
    form.addEventListener("submit", sendMessage);
    ui.main.appendChild(meta);
    ui.main.appendChild(transcript);
    ui.main.appendChild(form);
    paintChat(chat, true);
  }

  function refreshOpenChat() {
    if (state.chatFlight || state.view !== "chat" || !state.openChat || !state.openChat.id) return;
    var id = state.openChat.id;
    var ticket = state.epoch;
    state.chatFlight = true;
    api("GET", "/api/chats/" + encodeURIComponent(id)).then(function (result) {
      state.chatFlight = false;
      if (ticket !== state.epoch || !result || result.stale) return;
      if (state.view !== "chat" || !state.openChat || state.openChat.id !== id) return;
      if (result.status === 404) {
        state.awaitingWork = false;
        if (state.openChat) state.openChat.working = false;
        ensureChatPoll();
        setComposerError("This chat is no longer available.");
        return;
      }
      if (!result.ok) {
        setComposerError(errorText(result, "Couldn't load this chat."));
        return;
      }
      var chat = normalizeChat(result.data, id);
      noteWork(chat);
      state.openChat = chat;
      paintChat(chat, false);
      ensureChatPoll();
    }).catch(function () {
      state.chatFlight = false;
    });
  }

  function noteWork(chat) {
    if (chat.working) {
      state.awaitingWork = false;
      state.idlePolls = 0;
      return;
    }
    if (!state.awaitingWork) return;
    state.idlePolls += 1;
    if (state.idlePolls >= 8) state.awaitingWork = false;
  }

  function ensureChatPoll() {
    var should = state.view === "chat" && state.openChat && (state.openChat.working || state.awaitingWork);
    if (should && !state.chatTimer) state.chatTimer = setInterval(refreshOpenChat, 1000);
    if (!should && state.chatTimer) {
      clearInterval(state.chatTimer);
      state.chatTimer = 0;
    }
  }

  function normalizeChat(data, id) {
    var source = data || {};
    var turns = Array.isArray(source.turns) ? source.turns : [];
    var subagents = Array.isArray(source.subagents) ? source.subagents : [];
    return {
      id: asString(source.id) || id,
      title: asString(source.title),
      provider: asString(source.provider),
      model: asString(source.model),
      status: asString(source.status),
      working: asBool(source.working),
      projectId: asString(source.projectId),
      liveReply: asText(source.liveReply),
      activityPhase: asString(source.activityPhase),
      turns: turns.map(function (turn) {
        return {
          prompt: asText(turn && turn.prompt),
          reply: asText(turn && turn.reply),
          outcome: asString(turn && turn.outcome),
          model: asString(turn && turn.model)
        };
      }),
      subagents: subagents.map(function (sub) {
        return {
          name: asString(sub && sub.name),
          state: asString(sub && sub.state),
          work: asText(sub && sub.work)
        };
      })
    };
  }

  function paintChat(chat, stick) {
    var title = document.getElementById("chat-title");
    if (title) title.textContent = chat.title || "Untitled chat";
    var stop = document.getElementById("stop");
    if (stop) stop.hidden = !chat.working;
    renderMeta(chat);
    renderTranscript(chat, stick);
  }

  function renderMeta(chat) {
    var meta = document.getElementById("chat-meta");
    if (!meta) return;
    clear(meta);
    var bits = [];
    if (chat.provider) bits.push(chat.provider);
    if (chat.model) bits.push(chat.model);
    if (bits.length) meta.appendChild(el("p", "status-line", bits.join(" · ")));
    if (chat.status) meta.appendChild(el("p", "status-line", chat.status));
    if (chat.projectId) meta.appendChild(el("p", "status-line", "Project " + chat.projectId));
    if (chat.activityPhase) meta.appendChild(el("p", "phase", "Activity: " + chat.activityPhase));
    if (chat.subagents.length) {
      meta.appendChild(el("h2", "section-label", "Subagents"));
      chat.subagents.forEach(function (sub) {
        var row = el("div", "subagent");
        row.appendChild(el("strong", "", sub.name || "Subagent"));
        if (sub.state) row.appendChild(el("div", "state", sub.state));
        if (sub.work) row.appendChild(el("p", "work", sub.work));
        meta.appendChild(row);
      });
    }
  }

  function renderTranscript(chat, forceStick) {
    var scroller = document.getElementById("transcript");
    if (!scroller) return;
    var distance = scroller.scrollHeight - scroller.scrollTop - scroller.clientHeight;
    var stick = forceStick || distance < 96;
    clear(scroller);
    if (!chat.turns.length && !chat.liveReply) {
      scroller.appendChild(el("p", "empty", "No messages yet."));
    }
    chat.turns.forEach(function (turn) {
      var block = el("article", "turn");
      if (turn.prompt) {
        block.appendChild(el("p", "who", "You"));
        block.appendChild(el("pre", "bubble you", turn.prompt));
      }
      if (turn.reply || turn.outcome || turn.model) {
        var who = turn.model ? "xAI Desk · " + turn.model : "xAI Desk";
        block.appendChild(el("p", "who", who));
        if (turn.reply) block.appendChild(el("pre", "bubble", turn.reply));
        if (turn.outcome) block.appendChild(el("p", "status-line", turn.outcome));
      }
      scroller.appendChild(block);
    });
    if (chat.liveReply) {
      var live = el("article", "turn");
      live.appendChild(el("p", "who", "xAI Desk · live"));
      live.appendChild(el("pre", "bubble live", chat.liveReply));
      scroller.appendChild(live);
    }
    if (stick) scroller.scrollTop = scroller.scrollHeight;
  }

  function setComposerError(message) {
    var node = document.getElementById("composer-error");
    if (!node) {
      if (message) showBanner(message);
      return;
    }
    node.textContent = message || "";
    node.hidden = !message;
  }

  function sendMessage(event) {
    event.preventDefault();
    if (state.sending || !state.openChat) return;
    var box = document.getElementById("message");
    var text = box ? box.value.trim() : "";
    if (!text) return;
    if (text.length > 16000) {
      setComposerError("Write a message of 1–16000 characters.");
      return;
    }
    state.sending = true;
    var send = document.getElementById("send");
    if (send) send.disabled = true;
    setComposerError("");
    var id = state.openChat.id;
    var ticket = state.epoch;
    api("POST", "/api/chats/" + encodeURIComponent(id) + "/messages", { text: text }).then(function (result) {
      state.sending = false;
      if (send) send.disabled = false;
      if (ticket !== state.epoch || !result || result.stale) return;
      if (!result.ok || !result.data || result.data.accepted !== true) {
        setComposerError(errorText(result, "The message was not accepted."));
        return;
      }
      if (box) box.value = "";
      state.awaitingWork = true;
      state.idlePolls = 0;
      ensureChatPoll();
      refreshOpenChat();
    }).catch(function () {
      state.sending = false;
      if (send) send.disabled = false;
    });
  }

  function stopChat() {
    if (state.stopping || !state.openChat) return;
    state.stopping = true;
    var stop = document.getElementById("stop");
    if (stop) stop.disabled = true;
    var id = state.openChat.id;
    var ticket = state.epoch;
    api("POST", "/api/chats/" + encodeURIComponent(id) + "/stop").then(function (result) {
      state.stopping = false;
      if (stop) stop.disabled = false;
      if (ticket !== state.epoch || !result || result.stale) return;
      if (!result.ok) {
        setComposerError(errorText(result, "Couldn't stop this chat."));
        return;
      }
      if (result.data && result.data.stopped === false) setComposerError("This chat is still running.");
      else setComposerError("");
      state.awaitingWork = false;
      refreshOpenChat();
    }).catch(function () {
      state.stopping = false;
      if (stop) stop.disabled = false;
    });
  }

  function showApprovals() {
    stopChatPoll();
    state.view = "approvals";
    state.openChat = null;
    hideBanner();
    setHeader([el("h1", "", "Approvals")]);
    if (state.approvals.length) renderApprovals(true);
    else {
      clear(ui.main);
      ui.main.appendChild(el("p", "empty", "Loading approvals…"));
    }
    syncTabs();
    refreshApprovals();
  }

  function refreshApprovals() {
    if (state.approvalFlight || !state.token) return;
    state.approvalFlight = true;
    var ticket = state.epoch;
    api("GET", "/api/approvals").then(function (result) {
      state.approvalFlight = false;
      if (ticket !== state.epoch || !result || result.stale) return;
      if (!result.ok) {
        var message = errorText(result, "Couldn't load approvals.");
        if (message !== state.approvalError && ui.banner.hidden) showBanner(message);
        state.approvalError = message;
        return;
      }
      state.approvalError = "";
      var approvals = normalizeApprovals(result.data && result.data.approvals);
      var alive = {};
      approvals.forEach(function (item) { alive[item.id] = true; });
      Object.keys(state.confirmIds).forEach(function (id) {
        if (!alive[id]) delete state.confirmIds[id];
      });
      state.approvals = approvals;
      syncTabs();
      if (state.view === "approvals") renderApprovals(false);
    }).catch(function () {
      state.approvalFlight = false;
    });
  }

  function normalizeApprovals(list) {
    if (!Array.isArray(list)) return [];
    return list.map(function (item) {
      return {
        id: asString(item && item.id),
        title: asString(item && item.title),
        kind: asString(item && item.kind),
        input: asText(item && item.input),
        destructive: asBool(item && item.destructive),
        origin: asString(item && item.origin),
        chatHint: asString(item && item.chatHint)
      };
    }).filter(function (item) { return item.id; });
  }

  function approvalSignature() {
    return state.approvals.map(function (item) {
      return [item.id, item.title, item.kind, item.input, item.destructive, item.origin, item.chatHint, state.confirmIds[item.id] ? "1" : "0", state.decisionId === item.id ? "1" : "0"].join("\u0001");
    }).join("\n");
  }

  function renderApprovals(force) {
    if (state.view !== "approvals") return;
    var sig = approvalSignature();
    if (!force && sig === state.approvalSig && document.getElementById("approval-list")) {
      focusConfirm();
      return;
    }
    state.approvalSig = sig;
    clear(ui.main);
    if (!state.approvals.length) {
      ui.main.appendChild(el("p", "empty", "No approvals waiting."));
      return;
    }
    var list = document.createElement("ul");
    list.id = "approval-list";
    list.className = "list";
    state.approvals.forEach(function (item) {
      var li = document.createElement("li");
      li.appendChild(approvalCard(item));
      list.appendChild(li);
    });
    ui.main.appendChild(list);
    focusConfirm();
  }

  function focusConfirm() {
    if (!state.focusConfirmId) return;
    var id = state.focusConfirmId;
    state.focusConfirmId = "";
    if (!/^[A-Za-z0-9_-]+$/.test(id)) return;
    var node = document.getElementById("confirm-" + id);
    if (node) node.focus();
  }

  function approvalCard(item) {
    var card = el("article", item.destructive ? "card destructive" : "card");
    card.appendChild(el("h2", "row-title", item.title || "Approval"));
    if (item.kind) card.appendChild(el("p", "status-line", item.kind));
    if (item.origin) card.appendChild(el("p", "status-line", item.origin));
    if (item.chatHint) card.appendChild(el("p", "status-line", item.chatHint));
    if (item.destructive) card.appendChild(el("p", "phase", "Destructive"));
    card.appendChild(el("h3", "section-label", "Input"));
    var input = el("pre", "approval-input", item.input);
    input.tabIndex = 0;
    input.setAttribute("aria-label", "Approval input");
    card.appendChild(input);
    var actions = el("div", "card-actions");
    var pending = state.decisionId === item.id;
    var deny = button("Deny", "btn danger", function () { decide(item, "deny", false); });
    deny.disabled = pending;
    actions.appendChild(deny);
    if (state.confirmIds[item.id]) {
      var confirm = button("Confirm destructive action", "btn warn", function () { decide(item, "allow", true); });
      confirm.disabled = pending;
      if (/^[A-Za-z0-9_-]+$/.test(item.id)) confirm.id = "confirm-" + item.id;
      actions.appendChild(confirm);
    } else {
      var allow = button("Allow", "btn", function () {
        if (item.destructive) {
          state.confirmIds[item.id] = true;
          state.focusConfirmId = item.id;
          renderApprovals(true);
          return;
        }
        decide(item, "allow", false);
      });
      allow.disabled = pending;
      actions.appendChild(allow);
    }
    card.appendChild(actions);
    return card;
  }

  function decide(item, decision, confirm) {
    if (state.decisionId) return;
    state.decisionId = item.id;
    renderApprovals(true);
    var ticket = state.epoch;
    api("POST", "/api/approvals/" + encodeURIComponent(item.id), { decision: decision, confirm: confirm }).then(function (result) {
      if (state.decisionId === item.id) state.decisionId = "";
      if (ticket !== state.epoch || !result || result.stale) return;
      if (result.status === 409 && result.data && result.data.error === "confirm_required") {
        state.confirmIds[item.id] = true;
        state.focusConfirmId = item.id;
        renderApprovals(true);
        return;
      }
      if (!result.ok || !result.data || result.data.accepted !== true) {
        showBanner(errorText(result, "The decision was not accepted."));
        renderApprovals(true);
        return;
      }
      delete state.confirmIds[item.id];
      refreshApprovals();
    }).catch(function () {
      if (state.decisionId === item.id) state.decisionId = "";
    });
  }

  function pollNotifications() {
    if (state.notifFlight || !state.token) return;
    state.notifFlight = true;
    var after = state.notifCursor == null ? "0" : String(state.notifCursor);
    var priming = !state.notifPrimed;
    var ticket = state.epoch;
    api("GET", "/api/notifications?after=" + encodeURIComponent(after)).then(function (result) {
      state.notifFlight = false;
      if (ticket !== state.epoch || !result || result.stale) return;
      if (!result.ok) return;
      var cursor = result.data ? result.data.cursor : null;
      if (typeof cursor === "number" && isFinite(cursor)) state.notifCursor = Math.trunc(cursor);
      else if (typeof cursor === "string" && /^\d+$/.test(cursor)) state.notifCursor = Number(cursor);
      state.notifPrimed = true;
      try {
        if (state.notifCursor != null) sessionStorage.setItem(CURSOR_KEY, String(state.notifCursor));
      } catch (err) { /* Cursor is optional. */ }
      if (state.view === "chats") loadChats();
      if (priming) return;
      var items = result.data && Array.isArray(result.data.items) ? result.data.items : [];
      var banners = [];
      var refreshChat = false;
      var refreshApproval = false;
      items.forEach(function (item) {
        var effect = presentNotice(item);
        if (effect.banner) banners.push(effect.banner);
        if (effect.refreshChat) refreshChat = true;
        if (effect.approval) refreshApproval = true;
      });
      if (banners.length) showBanner(banners);
      if (refreshChat) refreshOpenChat();
      if (refreshApproval) refreshApprovals();
    }).catch(function () {
      state.notifFlight = false;
    });
  }

  function presentNotice(item) {
    var kind = asString(item && item.kind);
    var text = asText(item && item.text) || (kind === "approval" ? "Approval waiting" : "Chat finished");
    var chatId = asString(item && item.chatId);
    var viewing = !document.hidden && state.view === "chat" && state.openChat && chatId && state.openChat.id === chatId;
    var effect = { banner: "", refreshChat: viewing, approval: kind === "approval" };
    if (document.hidden && typeof Notification === "function" && Notification.permission === "granted") postNotify(text);
    else effect.banner = text;
    return effect;
  }

  function postNotify(body) {
    var message = { type: "notify", title: "xAI Desk", body: body };
    if (!("serviceWorker" in navigator)) {
      try { new Notification("xAI Desk", { body: body }); } catch (err) { /* Permission can change. */ }
      return;
    }
    var worker = navigator.serviceWorker.controller;
    if (worker) {
      worker.postMessage(message);
      return;
    }
    navigator.serviceWorker.ready.then(function (registration) {
      if (registration.active) registration.active.postMessage(message);
      else registration.showNotification("xAI Desk", { body: body, icon: "/icon-192.png" });
    }).catch(function () {});
  }

  function boot() {
    var splash = document.getElementById("splash");
    if (splash) splash.remove();
    mount();
    try { state.token = readToken(); } catch (err) { state.token = ""; }
    try {
      var saved = sessionStorage.getItem(CURSOR_KEY);
      if (saved != null && /^\d+$/.test(saved)) {
        state.notifCursor = Number(saved);
        state.notifPrimed = true;
      }
    } catch (err) { /* Start from cursor 0. */ }
    state.pairCode = pairingCodeFromHash();
    document.addEventListener("visibilitychange", function () {
      if (document.hidden || !state.token) return;
      if (state.view === "chat") refreshOpenChat();
      pollNotifications();
      refreshApprovals();
    });
    window.addEventListener("hashchange", function () {
      var code = pairingCodeFromHash();
      if (!code) return;
      state.pairCode = code;
      renderPair();
    });
    if (state.pairCode) {
      renderPair();
      return;
    }
    if (!state.token) {
      renderScan();
      return;
    }
    enterApp();
  }

  boot();
})();
