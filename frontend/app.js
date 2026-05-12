(function () {
  const els = {
    statusPill: document.getElementById("statusPill"),
    searchInput: document.getElementById("searchInput"),
    searchBtn: document.getElementById("searchBtn"),
    prevSearchPageBtn: document.getElementById("prevSearchPageBtn"),
    nextSearchPageBtn: document.getElementById("nextSearchPageBtn"),
    idsInput: document.getElementById("idsInput"),
    baseDirInput: document.getElementById("baseDirInput"),
    selectDirBtn: document.getElementById("selectDirBtn"),
    openDirBtn: document.getElementById("openDirBtn"),
    openSettingsBtn: document.getElementById("openSettingsBtn"),
    closeSettingsBtn: document.getElementById("closeSettingsBtn"),
    cancelSettingsBtn: document.getElementById("cancelSettingsBtn"),
    saveSettingsBtn: document.getElementById("saveSettingsBtn"),
    settingsModal: document.getElementById("settingsModal"),
    settingsSummary: document.getElementById("settingsSummary"),
    imageFormatSelect: document.getElementById("imageFormatSelect"),
    outputFormatSelect: document.getElementById("outputFormatSelect"),
    pdfModeSelect: document.getElementById("pdfModeSelect"),
    photoThreadsInput: document.getElementById("photoThreadsInput"),
    imageThreadsInput: document.getElementById("imageThreadsInput"),
    autoPathInput: document.getElementById("autoPathInput"),
    startDownloadBtn: document.getElementById("startDownloadBtn"),
    stopDownloadBtn: document.getElementById("stopDownloadBtn"),
    resultsTitle: document.getElementById("resultsTitle"),
    resultsMeta: document.getElementById("resultsMeta"),
    resultsList: document.getElementById("resultsList"),
    loadCoverBtn: document.getElementById("loadCoverBtn"),
    coverImage: document.getElementById("coverImage"),
    coverPlaceholder: document.getElementById("coverPlaceholder"),
    coverBox: document.querySelector(".cover-box"),
    detailList: document.getElementById("detailList"),
    taskStatus: document.getElementById("taskStatus"),
    toggleLogScrollBtn: document.getElementById("toggleLogScrollBtn"),
    clearLogBtn: document.getElementById("clearLogBtn"),
    downloadQueue: document.getElementById("downloadQueue"),
    logPanel: document.getElementById("logPanel"),
  };

  let selectedIds = new Set();
  let lastResults = [];
  let currentSearchText = "";
  let currentSearchPage = 1;
  let currentResultMode = "empty";
  let currentTaskSnapshot = null;
  let lastDownloadPayload = null;
  let isApplyingConfig = false;
  let activeConfig = null;
  let settingsDraft = null;
  let logAutoScrollPaused = false;
  const authToken = window.__JMDOWNLOAD_TOKEN__ || "";
  const isDesktop = !!window.chrome?.webview;
  const bridgeCallbacks = new Map();

  function callDesktop(type, payload, timeoutMs = 30000) {
    if (!isDesktop) {
      return Promise.reject(new Error("当前不是 WebView2 桌面模式"));
    }

    const id = `${Date.now()}-${Math.random().toString(16).slice(2)}`;
    const message = { id, type, payload: payload || {} };

    return new Promise((resolve, reject) => {
      bridgeCallbacks.set(id, { resolve, reject });
      window.chrome.webview.postMessage(message);
      window.setTimeout(() => {
        if (bridgeCallbacks.has(id)) {
          bridgeCallbacks.delete(id);
          reject(new Error("桌面桥接响应超时"));
        }
      }, timeoutMs);
    });
  }

  window.addEventListener("jm-desktop-response", (event) => {
    const detail = event.detail || {};
    const callback = bridgeCallbacks.get(detail.id);
    if (!callback) {
      return;
    }

    bridgeCallbacks.delete(detail.id);
    if (detail.ok) {
      callback.resolve(detail.data || {});
    } else {
      callback.reject(new Error(detail.error || "桌面桥接调用失败"));
    }
  });

  function parseIds(text) {
    const seen = new Set();
    const ids = [];
    String(text || "")
      .split(",")
      .flatMap((part) => part.trim().split(/\s+/))
      .forEach((id) => {
        if (id && !seen.has(id)) {
          seen.add(id);
          ids.push(id);
        }
      });
    return ids;
  }

  function syncIdsInput() {
    els.idsInput.value = Array.from(selectedIds).join(",");
    updateResultChecks();
  }

  function syncSelectedFromInput() {
    selectedIds = new Set(parseIds(els.idsInput.value));
    updateResultChecks();
  }

  function appendLog(message, level) {
    const shouldStickToBottom = !logAutoScrollPaused && isLogNearBottom();
    const line = document.createElement("div");
    line.className = "log-" + String(level || "info").toLowerCase();
    line.textContent = `[${new Date().toLocaleTimeString()}][${level || "INFO"}] ${message}`;
    els.logPanel.appendChild(line);
    if (shouldStickToBottom) {
      scrollLogToBottom();
    }
  }

  function isLogNearBottom() {
    const distance = els.logPanel.scrollHeight - els.logPanel.scrollTop - els.logPanel.clientHeight;
    return distance < 32;
  }

  function scrollLogToBottom() {
    els.logPanel.scrollTop = els.logPanel.scrollHeight;
  }

  function setLogAutoScrollPaused(paused) {
    logAutoScrollPaused = paused;
    els.toggleLogScrollBtn.textContent = paused ? "继续滚动" : "暂停滚动";
    els.toggleLogScrollBtn.classList.toggle("active", paused);
    if (!paused) {
      scrollLogToBottom();
    }
  }

  function clearLogs() {
    els.logPanel.replaceChildren();
    appendLog("日志已清空", "INFO");
  }

  function statusLabel(status) {
    return {
      queued: "等待中",
      running: "下载中",
      success: "完成",
      failed: "失败",
      cancelled: "已取消",
    }[status] || status || "未知";
  }

  function progressText(task) {
    const progress = Number(task.progress || 0);
    const total = Number(task.total || 0);
    if (total <= 0) {
      return "";
    }
    const percent = Math.min(100, Math.max(0, Math.round((progress / total) * 100)));
    return `${progress}/${total} · ${percent}%`;
  }

  async function api(path, options) {
    const requestOptions = Object.assign({}, options || {});
    requestOptions.headers = Object.assign({}, requestOptions.headers || {});
    if (authToken) {
      requestOptions.headers.Authorization = `Bearer ${authToken}`;
    }

    const response = await fetch(path, requestOptions);
    if (!response.ok) {
      let message = response.statusText;
      try {
        const payload = await response.json();
        message = payload.detail || message;
      } catch (error) {
        // Keep the HTTP status text if the body is not JSON.
      }
      throw new Error(message);
    }
    return response.json();
  }

  function readSettings() {
    return {
      base_dir: els.baseDirInput.value.trim() || "JMDownLoad",
      image_format: els.imageFormatSelect.value,
      output_format: els.outputFormatSelect.value,
      pdf_mode: els.pdfModeSelect.value,
      photo_threads: Number(els.photoThreadsInput.value || 1),
      image_threads: Number(els.imageThreadsInput.value || 5),
      auto_path: els.autoPathInput.checked,
    };
  }

  function formatOutputLabel(value) {
    return {
      images: "图片目录",
      zip: "ZIP",
      pdf: "PDF",
    }[value] || value || "图片目录";
  }

  function formatPdfModeLabel(value) {
    return {
      merged: "全部汇总",
      chapters: "章节分开",
    }[value] || "全部汇总";
  }

  function updateSettingsSummary(config) {
    const settings = config || readSettings();
    const parts = [
      formatOutputLabel(settings.output_format),
      settings.image_format || ".png",
      `${settings.photo_threads || 1}/${settings.image_threads || 5} 线程`,
    ];

    if (settings.output_format === "pdf") {
      parts.push(formatPdfModeLabel(settings.pdf_mode));
    }

    const path = settings.base_dir || "JMDownLoad";
    els.settingsSummary.textContent = `${parts.join(" · ")} · ${path}`;
    els.settingsSummary.title = path;
  }

  function applySettings(config) {
    isApplyingConfig = true;
    activeConfig = Object.assign({}, config);
    els.baseDirInput.value = config.base_dir || "JMDownLoad";
    els.imageFormatSelect.value = config.image_format || ".png";
    els.outputFormatSelect.value = config.output_format || "images";
    els.pdfModeSelect.value = config.pdf_mode || "merged";
    els.photoThreadsInput.value = config.photo_threads || 1;
    els.imageThreadsInput.value = config.image_threads || 5;
    els.autoPathInput.checked = config.auto_path !== false;
    isApplyingConfig = false;
    updateSettingsSummary(config);
  }

  async function loadConfig() {
    try {
      const config = await api("/api/config");
      applySettings(config);
      appendLog("已加载本地配置", "INFO");
    } catch (error) {
      appendLog(`加载配置失败: ${error.message}`, "WARNING");
    }
  }

  async function saveConfigNow() {
    if (isApplyingConfig) {
      return null;
    }

    try {
      const config = await api("/api/config", {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(readSettings()),
      });
      applySettings(config);
      appendLog("设置已保存", "INFO");
      return config;
    } catch (error) {
      appendLog(`保存配置失败: ${error.message}`, "ERROR");
      return null;
    }
  }

  function openSettingsDialog() {
    settingsDraft = Object.assign({}, activeConfig || readSettings());
    if (activeConfig) {
      applySettings(activeConfig);
    }
    els.settingsModal.hidden = false;
    els.baseDirInput.focus();
  }

  function closeSettingsDialog(saveChanges) {
    if (saveChanges) {
      return saveConfigNow().then((config) => {
        if (config) {
          els.settingsModal.hidden = true;
          settingsDraft = null;
        }
      });
    }

    if (settingsDraft) {
      applySettings(settingsDraft);
    }
    els.settingsModal.hidden = true;
    settingsDraft = null;
    return Promise.resolve();
  }

  function setStatus(ok, text) {
    els.statusPill.textContent = text;
    els.statusPill.classList.toggle("ok", ok);
    els.statusPill.classList.toggle("bad", !ok);
  }

  async function checkHealth() {
    try {
      const payload = await api("/health");
      setStatus(true, "后端已连接");
      updateTaskStatus(payload.download);
    } catch (error) {
      setStatus(false, "后端不可用");
    }
  }

  function setResultsLoading(title) {
    els.resultsTitle.textContent = title;
    els.resultsMeta.textContent = "加载中";
    updateSearchPager(false);
    els.resultsList.className = "result-list empty-state";
    els.resultsList.textContent = "正在加载";
  }

  function updateSearchPager(hasNext = false) {
    const isSearch = currentResultMode === "search";
    els.prevSearchPageBtn.disabled = !isSearch || currentSearchPage <= 1;
    els.nextSearchPageBtn.disabled = !isSearch || !hasNext;
  }

  function renderResults(title, items, options = {}) {
    lastResults = items || [];
    currentResultMode = options.mode || currentResultMode;
    els.resultsTitle.textContent = title;
    els.resultsMeta.textContent = options.meta || `${lastResults.length} 条`;
    updateSearchPager(!!options.hasNext);
    els.resultsList.className = "result-list";
    els.resultsList.replaceChildren();

    if (!lastResults.length) {
      els.resultsList.classList.add("empty-state");
      els.resultsList.textContent = "无数据";
      return;
    }

    lastResults.forEach((item) => {
      const row = document.createElement("label");
      row.className = "result-item";

      const checkbox = document.createElement("input");
      checkbox.type = "checkbox";
      checkbox.checked = selectedIds.has(String(item.id));
      checkbox.dataset.id = item.id;

      const idBox = document.createElement("div");
      idBox.className = "result-id";
      idBox.textContent = `ID ${item.id}`;

      const titleBox = document.createElement("div");
      titleBox.className = "result-title";
      titleBox.textContent = item.rank ? `${item.rank}. ${item.title}` : item.title;

      const coverBtn = document.createElement("button");
      coverBtn.type = "button";
      coverBtn.className = "result-cover-btn";
      coverBtn.textContent = "封面";
      coverBtn.title = "加载封面和详情";

      checkbox.addEventListener("change", () => {
        if (checkbox.checked) {
          selectedIds.add(String(item.id));
        } else {
          selectedIds.delete(String(item.id));
        }
        syncIdsInput();
      });

      coverBtn.addEventListener("click", (event) => {
        event.preventDefault();
        loadCoverAndDetail(String(item.id));
      });

      row.append(checkbox, idBox, titleBox, coverBtn);
      els.resultsList.appendChild(row);
    });
  }

  function updateResultChecks() {
    els.resultsList.querySelectorAll("input[type='checkbox']").forEach((checkbox) => {
      checkbox.checked = selectedIds.has(String(checkbox.dataset.id));
    });
  }

  async function runSearch(page = 1) {
    const q = els.searchInput.value.trim();
    if (!q) {
      appendLog("请输入搜索关键词", "WARNING");
      return;
    }
    currentSearchText = q;
    currentSearchPage = Math.max(1, page);
    currentResultMode = "search";
    setResultsLoading(`搜索: ${q}`);
    try {
      const payload = await api(`/api/search?q=${encodeURIComponent(q)}&page=${currentSearchPage}`);
      renderResults(`搜索: ${q}`, payload.items, {
        mode: "search",
        meta: `第 ${currentSearchPage} 页，${payload.items.length} 条`,
        hasNext: payload.has_next,
      });
    } catch (error) {
      renderResults(`搜索: ${q}`, [], {
        mode: "search",
        meta: `第 ${currentSearchPage} 页，加载失败`,
        hasNext: false,
      });
      appendLog(`搜索失败: ${error.message}`, "ERROR");
    }
  }

  async function loadRanking(type) {
    currentResultMode = "ranking";
    updateSearchPager(false);
    document.querySelectorAll(".rank-tab").forEach((button) => {
      button.classList.toggle("active", button.dataset.rank === type);
    });
    const titleMap = { day: "日榜", week: "周榜", month: "月榜" };
    setResultsLoading(titleMap[type] || "排行榜");
    try {
      const payload = await api(`/api/ranking?type=${encodeURIComponent(type)}`);
      renderResults(titleMap[type] || "排行榜", payload.items, {
        mode: "ranking",
        meta: `${payload.items.length} 条`,
        hasNext: false,
      });
    } catch (error) {
      renderResults(titleMap[type] || "排行榜", [], {
        mode: "ranking",
        meta: "加载失败",
        hasNext: false,
      });
      appendLog(`排行榜加载失败: ${error.message}`, "ERROR");
    }
  }

  function selectedFirstAlbumId() {
    const ids = parseIds(els.idsInput.value);
    return ids.find((id) => !id.toLowerCase().startsWith("p")) || "";
  }

  async function loadCoverAndDetail(albumId) {
    if (!albumId) {
      albumId = selectedFirstAlbumId();
    }
    if (!albumId) {
      appendLog("请选择或输入本子 ID", "WARNING");
      return;
    }

    els.coverBox.classList.remove("has-image");
    els.coverImage.removeAttribute("src");
    els.coverPlaceholder.textContent = "正在加载";
    els.detailList.replaceChildren();

    try {
      const detail = await api(`/api/album/${encodeURIComponent(albumId)}`);
      renderDetail(detail);
      els.coverImage.onload = () => {
        els.coverBox.classList.add("has-image");
      };
      els.coverImage.onerror = () => {
        els.coverBox.classList.remove("has-image");
        els.coverPlaceholder.textContent = "封面加载失败";
      };
      els.coverImage.src = `/api/cover/${encodeURIComponent(albumId)}?t=${Date.now()}`;
    } catch (error) {
      els.coverPlaceholder.textContent = "加载失败";
      appendLog(`详情或封面加载失败: ${error.message}`, "ERROR");
    }
  }

  function renderDetail(detail) {
    const rows = [
      ["ID", detail.id || ""],
      ["标题", detail.title || ""],
      ["作者", detail.author || ""],
      ["页数", detail.page_count || ""],
      ["标签", Array.isArray(detail.tags) ? detail.tags.join(", ") : ""],
    ];

    rows.forEach(([key, value]) => {
      const row = document.createElement("div");
      const dt = document.createElement("dt");
      const dd = document.createElement("dd");
      dt.textContent = key;
      dd.textContent = value || "-";
      row.append(dt, dd);
      els.detailList.appendChild(row);
    });
  }

  async function startDownload() {
    syncSelectedFromInput();
    const ids = Array.from(selectedIds);
    if (!ids.length) {
      appendLog("请输入要下载的 ID", "WARNING");
      return;
    }

    await saveConfigNow();

    const payload = Object.assign({
      ids,
    }, readSettings());

    try {
      lastDownloadPayload = payload;
      const response = await api("/api/download", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(payload),
      });
      appendLog(`已提交下载: ${response.ids.join(", ")}`, "INFO");
      pollTaskStatus();
    } catch (error) {
      appendLog(`启动下载失败: ${error.message}`, "ERROR");
    }
  }

  async function stopDownload() {
    try {
      const payload = await api("/api/download/stop", { method: "POST" });
      appendLog(payload.stopping ? "已发送强制停止请求" : "当前没有下载任务", "WARNING");
      pollTaskStatus();
    } catch (error) {
      appendLog(`停止下载失败: ${error.message}`, "ERROR");
    }
  }

  async function retryDownloadId(itemId) {
    const basePayload = lastDownloadPayload || {
      ...readSettings(),
    };

    const payload = Object.assign({}, basePayload, { ids: [itemId] });
    try {
      lastDownloadPayload = payload;
      const response = await api("/api/download", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(payload),
      });
      appendLog(`已重试下载: ${response.ids.join(", ")}`, "INFO");
      pollTaskStatus();
    } catch (error) {
      appendLog(`重试失败: ${error.message}`, "ERROR");
    }
  }

  async function selectDownloadDirectory() {
    try {
      const result = await callDesktop("selectDirectory", { path: els.baseDirInput.value.trim() }, 10 * 60 * 1000);
      if (!result.cancelled && result.path) {
        els.baseDirInput.value = result.path;
        appendLog(`已选择下载目录: ${result.path}`, "INFO");
        updateSettingsSummary();
      }
    } catch (error) {
      appendLog(error.message, "WARNING");
    }
  }

  async function openDownloadDirectory() {
    try {
      const path = els.baseDirInput.value.trim();
      if (!path) {
        appendLog("下载路径为空", "WARNING");
        return;
      }
      const result = await callDesktop("openDirectory", { path });
      appendLog(`已打开下载目录: ${result.path || path}`, "INFO");
    } catch (error) {
      appendLog(`打开下载目录失败: ${error.message}`, "ERROR");
    }
  }

  function updateTaskStatus(task) {
    if (!task) {
      return;
    }
    currentTaskSnapshot = task;
    els.stopDownloadBtn.disabled = !task.running;
    els.startDownloadBtn.disabled = task.running;
    if (task.running) {
      els.taskStatus.textContent = task.stopping ? "停止中" : `下载中 ${task.current_item_id || ""}`;
    } else if (task.last_failed_ids && task.last_failed_ids.length) {
      els.taskStatus.textContent = `空闲，失败 ${task.last_failed_ids.length} 个`;
    } else {
      els.taskStatus.textContent = "空闲";
    }
    renderDownloadQueue(task.tasks || []);
  }

  function renderDownloadQueue(tasks) {
    els.downloadQueue.replaceChildren();
    const visibleTasks = (tasks || []).filter((task) => task.status !== "success");

    if (!visibleTasks.length) {
      els.downloadQueue.className = "download-queue empty-state";
      els.downloadQueue.textContent = "暂无下载任务";
      return;
    }

    els.downloadQueue.className = "download-queue";
    visibleTasks.forEach((task) => {
      const row = document.createElement("div");
      row.className = "queue-row";

      const badge = document.createElement("span");
      badge.className = `queue-badge ${task.status || "queued"}`;
      badge.textContent = statusLabel(task.status);

      const main = document.createElement("div");
      main.className = "queue-main";

      const id = document.createElement("div");
      id.className = "queue-id";
      id.textContent = task.item_id || "";

      const path = document.createElement("div");
      path.className = "queue-path";
      path.title = task.base_dir || "";
      path.textContent = task.base_dir || "-";

      const message = document.createElement("div");
      message.className = "queue-message";
      message.title = task.message || "";
      message.textContent = task.message || "";

      const progress = document.createElement("div");
      progress.className = "queue-progress";
      const progressBar = document.createElement("span");
      const total = Number(task.total || 0);
      const done = Number(task.progress || 0);
      progressBar.style.width = total > 0 ? `${Math.min(100, Math.max(0, (done / total) * 100))}%` : "0%";
      progress.appendChild(progressBar);

      const progressMeta = document.createElement("div");
      progressMeta.className = "queue-progress-meta";
      progressMeta.textContent = [progressText(task), task.detail].filter(Boolean).join(" · ");

      main.append(id, path);
      if (task.message) {
        main.appendChild(message);
      }
      if (task.total > 0) {
        main.append(progress, progressMeta);
      }

      const actionBox = document.createElement("div");
      if (task.status === "failed") {
        const retryBtn = document.createElement("button");
        retryBtn.type = "button";
        retryBtn.className = "queue-action";
        retryBtn.textContent = "重试";
        retryBtn.disabled = !!currentTaskSnapshot?.running;
        retryBtn.addEventListener("click", () => retryDownloadId(task.item_id));
        actionBox.appendChild(retryBtn);
      }

      row.append(badge, main, actionBox);
      els.downloadQueue.appendChild(row);
    });
  }

  async function pollTaskStatus() {
    try {
      const task = await api("/api/tasks");
      updateTaskStatus(task);
    } catch (error) {
      appendLog(`状态刷新失败: ${error.message}`, "ERROR");
    }
  }

  function connectEvents() {
    const protocol = window.location.protocol === "https:" ? "wss:" : "ws:";
    const tokenQuery = authToken ? `?token=${encodeURIComponent(authToken)}` : "";
    const socket = new WebSocket(`${protocol}//${window.location.host}/ws/events${tokenQuery}`);

    socket.addEventListener("message", (event) => {
      const payload = JSON.parse(event.data);
      if (payload.type !== "item_progress" || payload.data?.stage !== "image_done") {
        appendLog(payload.message, payload.level);
      }
      if (payload.data && payload.data.snapshot) {
        updateTaskStatus(payload.data.snapshot);
      }
      if (payload.type === "item_success" && payload.item_id) {
        selectedIds.delete(String(payload.item_id));
        syncIdsInput();
      }
      if (payload.type === "finished") {
        pollTaskStatus();
      }
    });

    socket.addEventListener("close", () => {
      setTimeout(connectEvents, 2000);
    });
  }

  els.searchBtn.addEventListener("click", () => runSearch(1));
  els.searchInput.addEventListener("keydown", (event) => {
    if (event.key === "Enter") {
      runSearch(1);
    }
  });
  els.prevSearchPageBtn.addEventListener("click", () => {
    if (currentResultMode === "search" && currentSearchPage > 1) {
      runSearch(currentSearchPage - 1);
    }
  });
  els.nextSearchPageBtn.addEventListener("click", () => {
    if (currentResultMode === "search" && currentSearchText) {
      runSearch(currentSearchPage + 1);
    }
  });
  els.idsInput.addEventListener("input", syncSelectedFromInput);
  els.loadCoverBtn.addEventListener("click", () => loadCoverAndDetail());
  els.openSettingsBtn.addEventListener("click", openSettingsDialog);
  els.closeSettingsBtn.addEventListener("click", () => closeSettingsDialog(false));
  els.cancelSettingsBtn.addEventListener("click", () => closeSettingsDialog(false));
  els.saveSettingsBtn.addEventListener("click", () => closeSettingsDialog(true));
  els.settingsModal.addEventListener("click", (event) => {
    if (event.target === els.settingsModal) {
      closeSettingsDialog(false);
    }
  });
  window.addEventListener("keydown", (event) => {
    if (event.key === "Escape" && !els.settingsModal.hidden) {
      closeSettingsDialog(false);
    }
  });
  els.selectDirBtn.addEventListener("click", selectDownloadDirectory);
  els.openDirBtn.addEventListener("click", openDownloadDirectory);
  els.startDownloadBtn.addEventListener("click", startDownload);
  els.stopDownloadBtn.addEventListener("click", stopDownload);
  els.toggleLogScrollBtn.addEventListener("click", () => {
    setLogAutoScrollPaused(!logAutoScrollPaused);
  });
  els.clearLogBtn.addEventListener("click", clearLogs);
  els.logPanel.addEventListener("scroll", () => {
    if (!logAutoScrollPaused && !isLogNearBottom()) {
      setLogAutoScrollPaused(true);
    }
  });
  [
    els.baseDirInput,
    els.imageFormatSelect,
    els.outputFormatSelect,
    els.pdfModeSelect,
    els.photoThreadsInput,
    els.imageThreadsInput,
    els.autoPathInput,
  ].forEach((element) => {
    element.addEventListener("change", () => updateSettingsSummary());
    element.addEventListener("input", () => updateSettingsSummary());
  });
  document.querySelectorAll(".rank-tab").forEach((button) => {
    button.addEventListener("click", () => loadRanking(button.dataset.rank));
  });

  checkHealth();
  loadConfig();
  if (!isDesktop) {
    els.selectDirBtn.disabled = true;
    els.openDirBtn.disabled = true;
    els.selectDirBtn.title = "浏览器模式不可用";
    els.openDirBtn.title = "浏览器模式不可用";
  }
  connectEvents();
  currentResultMode = "empty";
  updateSearchPager(false);
  setInterval(pollTaskStatus, 2500);
})();
