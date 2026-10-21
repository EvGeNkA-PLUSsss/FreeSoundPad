(() => {
    "use strict";

    const TOKEN_KEY = "fsp_remote_token";
    const PAGE_SIZE = 48;
    const $ = (selector) => document.querySelector(selector);

    const els = {
        login: $("#login"),
        loginForm: $("#loginForm"),
        tokenInput: $("#tokenInput"),
        loginError: $("#loginError"),
        app: $("#app"),
        connection: $("#connection"),
        connectionText: $("#connectionText"),
        nowCard: $("#nowCard"),
        nowTitle: $("#nowTitle"),
        nowMeta: $("#nowMeta"),
        nowTime: $("#nowTime"),
        nowProgress: $("#nowProgress"),
        nowState: $("#nowState"),
        stopNow: $("#stopNow"),
        stopAll: $("#stopAll"),
        search: $("#searchInput"),
        category: $("#categorySelect"),
        padsGrid: $("#padsGrid"),
        empty: $("#empty"),
        loadMore: $("#loadMore"),
        libraryCount: $("#libraryCount"),
        settingsOpen: $("#settingsOpen"),
        favoritesOpen: $("#favoritesOpen"),
        favoritesCount: $("#favoritesCount"),
        favLayer: $("#favLayer"),
        favList: $("#favList"),
        favEmpty: $("#favEmpty"),
        favCount: $("#favCount"),
        favClose: $("#favClose"),
        favBackdrop: $("#favBackdrop"),
        sheetLayer: $("#sheetLayer"),
        sheet: $("#sheet"),
        sheetClose: $("#sheetClose"),
        sheetBackdrop: $("#sheetBackdrop"),
        browserTitle: $("#browserTitle"),
        browserUrl: $("#browserUrl"),
        navForm: $("#navForm"),
        navInput: $("#navInput"),
        outputDevice: $("#outputDevice"),
        outputHint: $("#outputHint"),
        toast: $("#toast"),
        micButton: $("#micButton"),
        browserButton: $("#browserButton"),
        monitorButton: $("#monitorButton"),
    };

    let token = readToken();
    let state = null;
    let booted = false;
    let refreshing = false;
    let query = "";
    let category = "all";
    let visibleLimit = PAGE_SIZE;
    let lastPadsSignature = "";
    let lastCategoriesSignature = "";
    let settingsDirtyUntil = 0;
    let toastTimer = 0;
    const rangeTimers = new Map();

    function readToken() {
        try {
            return localStorage.getItem(TOKEN_KEY) || "";
        } catch {
            return "";
        }
    }

    function storeToken(value) {
        token = value;
        try {
            if (value) localStorage.setItem(TOKEN_KEY, value);
            else localStorage.removeItem(TOKEN_KEY);
        } catch {
            // private mode / storage blocked
        }
    }

    async function api(path, options = {}) {
        const controller = new AbortController();
        const timeout = window.setTimeout(() => controller.abort(), 8000);
        const headers = { ...(options.headers || {}), "X-FSP-Token": token };

        if (options.body && !headers["Content-Type"]) {
            headers["Content-Type"] = "application/json";
        }

        try {
            const response = await fetch(path, {
                ...options,
                headers,
                signal: controller.signal,
            });

            if (response.status === 401) {
                const error = new Error("unauthorized");
                error.code = "auth";
                throw error;
            }

            const contentType = response.headers.get("content-type") || "";
            const payload = contentType.includes("application/json")
                ? await response.json()
                : await response.text();

            if (!response.ok) {
                const error = new Error("http " + response.status);
                error.code = "http";
                error.payload = payload;
                throw error;
            }

            return payload;
        } catch (error) {
            if (error.name === "AbortError") {
                error.code = "timeout";
            }
            throw error;
        } finally {
            window.clearTimeout(timeout);
        }
    }

    function formatError(error) {
        if (error?.code === "timeout") return "Приложение не отвечает";
        if (error?.code === "auth") return "Неверный код доступа";
        return "Нет связи с приложением";
    }

    function showLogin(message = "") {
        els.app.hidden = true;
        els.login.hidden = false;
        els.loginError.textContent = message;
        closeSheet();
        closeFavorites();
        setConnection(false, "не подключено");
        window.setTimeout(() => els.tokenInput.focus(), 40);
    }

    function showApp() {
        els.login.hidden = true;
        els.app.hidden = false;
        booted = true;
    }

    function setConnection(online, text) {
        els.connection.classList.toggle("is-online", online);
        els.connection.classList.toggle("is-offline", !online);
        els.connectionText.textContent = text;
    }

    function toast(message) {
        els.toast.textContent = message;
        els.toast.classList.add("is-visible");
        window.clearTimeout(toastTimer);
        toastTimer = window.setTimeout(() => els.toast.classList.remove("is-visible"), 2200);
    }

    function authLost() {
        storeToken("");
        state = null;
        booted = false;
        showLogin("Код не подошёл. Возьми новый из настроек FreeSoundPad.");
    }

    async function tryLogin() {
        const candidate = els.tokenInput.value.trim().toUpperCase();
        if (!candidate) {
            els.loginError.textContent = "Введи код доступа";
            els.tokenInput.focus();
            return;
        }

        els.loginError.textContent = "";
        const button = els.loginForm.querySelector("button");
        button.disabled = true;
        button.textContent = "Проверяю…";

        try {
            const response = await fetch("/api/state", {
                headers: { "X-FSP-Token": candidate },
                cache: "no-store",
            });

            if (response.status === 401) {
                els.loginError.textContent = "Неверный код. Проверь настройки FreeSoundPad.";
                return;
            }

            if (!response.ok) {
                els.loginError.textContent = "Сервер ответил ошибкой " + response.status;
                return;
            }

            const firstState = await response.json();
            storeToken(candidate);
            state = firstState;
            showApp();
            setConnection(true, "подключено");
            renderAll();
            window.setTimeout(refreshState, 400);
        } catch {
            els.loginError.textContent = "Не удалось подключиться. Телефон в той же Wi-Fi?";
        } finally {
            button.disabled = false;
            button.textContent = "Подключиться";
        }
    }

    async function refreshState() {
        if (refreshing || !token) return;
        refreshing = true;
        try {
            const fresh = await api("/api/state");
            state = fresh;
            if (!booted) showApp();
            setConnection(true, "подключено");
            renderAll();
        } catch (error) {
            if (error.code === "auth") {
                authLost();
            } else if (booted) {
                setConnection(false, "нет связи");
            }
        } finally {
            refreshing = false;
        }
    }

    function renderAll() {
        if (!state) return;
        renderNow();
        renderCategories();
        renderPads();
        renderFavorites();
        renderSettings();
        renderDock();
    }

    function renderNow() {
        const now = state.nowPlaying || {};
        const playing = Boolean(now.isPlaying);
        els.nowTitle.textContent = now.title || "Тишина";
        els.nowMeta.textContent = now.meta || "Выбери звук";
        els.nowTime.textContent = now.time || "—";
        els.nowState.textContent = playing ? "в эфире" : "тишина";
        els.nowCard.classList.toggle("is-playing", playing);
        els.nowProgress.style.width = Math.max(0, Math.min(100, Number(now.progress) || 0)) + "%";
    }

    function renderCategories() {
        const pads = state.pads || [];
        const names = [...new Set(pads.map((pad) => pad.category).filter(Boolean))]
            .sort((a, b) => a.localeCompare(b, "ru"));
        const signature = names.join("\u0001");

        if (signature !== lastCategoriesSignature) {
            lastCategoriesSignature = signature;
            const current = category;
            els.category.replaceChildren();
            addCategoryOption("all", "Все");
            addCategoryOption("favorites", "Избранное");
            names.forEach((name) => addCategoryOption(name, name));

            if (current !== "all" && current !== "favorites" && !names.includes(current)) {
                category = "all";
            }

            els.category.value = category;
        }
    }

    function addCategoryOption(value, label) {
        const option = document.createElement("option");
        option.value = value;
        option.textContent = label;
        els.category.append(option);
    }

    function filteredPads() {
        const pads = state.pads || [];
        const needle = query.trim().toLowerCase();

        return pads.filter((pad) => {
            if (category === "favorites" && !pad.favorite) return false;
            if (category !== "all" && category !== "favorites" && pad.category !== category) return false;
            if (!needle) return true;

            return (pad.title || "").toLowerCase().includes(needle)
                || (pad.category || "").toLowerCase().includes(needle)
                || (pad.hotkey || "").toLowerCase().includes(needle);
        });
    }

    function renderPads(force = false) {
        const pads = filteredPads();
        const playingId = state.nowPlaying?.isPlaying ? state.nowPlaying.padId : null;
        const signature = `${query}|${category}|${visibleLimit}|${playingId}|${pads.length}|${state.pads.length}`;

        els.libraryCount.textContent = pads.length === state.pads.length
            ? `${pads.length} звуков`
            : `${pads.length} из ${state.pads.length}`;

        const visible = pads.slice(0, visibleLimit);

        if (force || signature !== lastPadsSignature) {
            lastPadsSignature = signature;
            els.padsGrid.replaceChildren();

            visible.forEach((pad) => {
                els.padsGrid.append(createPad(pad, playingId === pad.id));
            });
        } else {
            els.padsGrid.querySelectorAll(".pad").forEach((node) => {
                const isPlaying = node.dataset.id === playingId;
                node.classList.toggle("is-playing", isPlaying);
                node.setAttribute("aria-pressed", String(isPlaying));
            });
        }

        els.empty.hidden = pads.length !== 0;
        els.loadMore.hidden = pads.length <= visibleLimit;
        if (!els.loadMore.hidden) {
            els.loadMore.textContent = `Показать ещё · ${visible.length} из ${pads.length}`;
        }
    }

    function favoritePads() {
        return (state.pads || []).filter((pad) => pad.favorite);
    }

    function renderFavorites() {
        if (!els.favLayer) return;

        const favorites = favoritePads();
        const playingId = state.nowPlaying?.isPlaying ? state.nowPlaying.padId : null;

        els.favoritesCount.textContent = String(favorites.length);
        els.favoritesCount.classList.toggle("has-items", favorites.length > 0);
        els.favCount.textContent = String(favorites.length);

        els.favEmpty.hidden = favorites.length !== 0;
        els.favList.hidden = favorites.length === 0;
        els.favList.replaceChildren();

        favorites.forEach((pad) => {
            const playing = playingId === pad.id;
            const row = document.createElement("button");
            row.type = "button";
            row.className = "fav-row" + (playing ? " is-playing" : "");
            row.style.setProperty("--pad", safeColor(pad.color));
            row.setAttribute("aria-pressed", String(playing));
            row.setAttribute("aria-label", (playing ? "Остановить " : "Включить ") + pad.title);

            const stripe = document.createElement("span");
            stripe.className = "fav-stripe";

            const copy = document.createElement("span");
            copy.className = "fav-copy";

            const title = document.createElement("strong");
            title.textContent = pad.title || "Без названия";

            const meta = document.createElement("small");
            meta.textContent = [pad.category || "разное", pad.duration].filter(Boolean).join(" · ");

            const play = document.createElement("span");
            play.className = "fav-play";
            play.innerHTML = playing
                ? '<svg viewBox="0 0 12 12" aria-hidden="true"><rect x="2.5" y="2.5" width="7" height="7" rx="1" fill="currentColor"/></svg>'
                : '<svg viewBox="0 0 12 12" aria-hidden="true"><path d="M3.4 2.4v7.2L9.6 6 3.4 2.4Z" fill="currentColor"/></svg>';

            copy.append(title, meta);
            row.append(stripe, copy, play);
            row.addEventListener("click", () => void togglePad(pad));
            els.favList.append(row);
        });
    }

    function createPad(pad, playing) {
        const button = document.createElement("button");
        button.type = "button";
        button.className = "pad" + (playing ? " is-playing" : "");
        button.dataset.id = pad.id;
        button.style.setProperty("--pad", safeColor(pad.color));
        button.setAttribute("aria-pressed", String(playing));
        button.setAttribute("aria-label", (playing ? "Остановить " : "Включить ") + pad.title);

        const top = document.createElement("span");
        top.className = "pad-top";

        const cat = document.createElement("span");
        cat.className = "pad-cat";
        cat.textContent = pad.category || "разное";

        const duration = document.createElement("span");
        duration.className = "pad-duration";
        duration.textContent = pad.duration || "";

        top.append(cat, duration);

        const wave = document.createElement("span");
        wave.className = "pad-wave";
        const seed = (pad.title || "").length + (pad.category || "").length;
        for (let index = 0; index < 11; index++) {
            const bar = document.createElement("i");
            bar.style.height = 18 + ((index * 37 + seed * 13) % 74) + "%";
            wave.append(bar);
        }

        const name = document.createElement("span");
        name.className = "pad-name";
        name.textContent = pad.title || "Без названия";

        if (pad.hotkey) {
            const hotkey = document.createElement("span");
            hotkey.className = "pad-hotkey";
            hotkey.textContent = pad.hotkey;
            button.append(top, wave, name, hotkey);
        } else {
            const play = document.createElement("span");
            play.className = "pad-play";
            play.innerHTML = playing
                ? '<svg viewBox="0 0 12 12" aria-hidden="true"><rect x="2.5" y="2.5" width="7" height="7" rx="1" fill="currentColor"/></svg>'
                : '<svg viewBox="0 0 12 12" aria-hidden="true"><path d="M3.4 2.4v7.2L9.6 6 3.4 2.4Z" fill="currentColor"/></svg>';
            button.append(top, wave, name, play);
        }

        button.addEventListener("click", () => togglePad(pad));
        return button;
    }

    function safeColor(value) {
        return /^#[0-9a-f]{6}$/i.test(value || "") ? value : "#48e0ae";
    }

    async function togglePad(pad) {
        if (!state) return;
        const playing = state.nowPlaying?.isPlaying && state.nowPlaying.padId === pad.id;
        const action = playing ? "stop" : "play";

        navigator.vibrate?.(8);

        try {
            await api(`/api/pads/${encodeURIComponent(pad.id)}/${action}`, { method: "POST" });

            if (playing) {
                state.nowPlaying = {
                    ...state.nowPlaying,
                    isPlaying: false,
                    padId: null,
                    progress: 0,
                    title: "Тишина",
                    meta: "Выбери звук",
                    time: "—",
                };
            } else {
                state.nowPlaying = {
                    title: pad.title,
                    meta: pad.loop ? "PAD · зациклено" : "PAD · веб-пульт",
                    time: "0:00 / " + (pad.duration || "—"),
                    progress: 0,
                    isPlaying: true,
                    padId: pad.id,
                };
            }

            renderNow();
            renderPads(true);
            renderFavorites();
            window.setTimeout(refreshState, 350);
        } catch (error) {
            toast(formatError(error));
            if (error.code === "auth") authLost();
        }
    }

    async function stopAll(silent = false) {
        try {
            await api("/api/stop-all", { method: "POST" });
            if (state) {
                state.nowPlaying = {
                    title: "Тишина",
                    meta: "Выбери звук",
                    time: "—",
                    progress: 0,
                    isPlaying: false,
                    padId: null,
                };
            }
            renderNow();
            renderPads(true);
            renderFavorites();
            if (!silent) toast("Все звуки остановлены");
            window.setTimeout(refreshState, 250);
        } catch (error) {
            toast(formatError(error));
            if (error.code === "auth") authLost();
        }
    }

    function setSwitchValue(key, value) {
        const input = document.querySelector(`[data-setting="${key}"]`);
        if (input) input.checked = Boolean(value);
    }

    async function patchSettings(partial) {
        settingsDirtyUntil = Date.now() + 1000;
        try {
            await api("/api/settings", {
                method: "POST",
                body: JSON.stringify(partial),
            });
            return true;
        } catch (error) {
            toast(formatError(error));
            if (error.code === "auth") authLost();
            window.setTimeout(refreshState, 250);
            return false;
        }
    }

    async function toggleSetting(key) {
        if (!state) return;
        const currentValue = Boolean(state.settings?.[key]);
        const nextValue = !currentValue;
        state.settings[key] = nextValue;
        setSwitchValue(key, nextValue);
        renderDock();

        const ok = await patchSettings({ [key]: nextValue });
        if (!ok) {
            state.settings[key] = currentValue;
            setSwitchValue(key, currentValue);
            renderDock();
        }
    }

    function renderSettings() {
        const settings = state.settings || {};

        document.querySelectorAll("[data-setting]").forEach((input) => {
            const key = input.dataset.setting;
            if (document.activeElement !== input) input.checked = Boolean(settings[key]);
            if (key === "monitorEnabled") input.disabled = !settings.canMonitor;
        });

        if (Date.now() > settingsDirtyUntil) {
            document.querySelectorAll("[data-gain]").forEach((input) => {
                if (document.activeElement === input) return;
                const key = input.dataset.gain;
                const value = Math.round(Number(settings[key]) || 0);
                input.value = String(value);
                const output = document.querySelector(`[data-output="${key}"]`);
                if (output) output.textContent = value + "%";
            });
        }

        els.browserTitle.textContent = state.browser?.title || "Не открыт";
        els.browserUrl.textContent = state.browser?.url || "—";
        els.outputDevice.textContent = state.output?.device || "Не выбрано";
        els.outputHint.textContent = state.output?.cable
            ? "Виртуальный кабель · можно отправлять в микрофон"
            : "Локальный режим · микрофон требует виртуальный кабель";
    }

    function renderDock() {
        setDockState(els.micButton, state.settings?.micPassthrough);
        setDockState(els.browserButton, state.settings?.browserSend);
        setDockState(els.monitorButton, state.settings?.monitorEnabled);
    }

    function setDockState(button, active) {
        if (!button) return;
        button.classList.toggle("is-active", Boolean(active));
        button.setAttribute("aria-pressed", String(Boolean(active)));
    }

    function openSheet() {
        closeFavorites();
        els.sheetLayer.hidden = false;
        document.body.style.overflow = "hidden";
        renderSettings();
        els.sheetClose.focus();
    }

    function closeSheet() {
        if (els.sheetLayer.hidden) return;
        els.sheetLayer.hidden = true;
        document.body.style.overflow = "";
        els.settingsOpen.focus();
    }

    function openFavorites() {
        closeSheet();
        els.favLayer.hidden = false;
        els.favoritesOpen.classList.add("is-active");
        document.body.style.overflow = "hidden";
        renderFavorites();
        els.favClose.focus();
    }

    function closeFavorites() {
        if (!els.favLayer || els.favLayer.hidden) return;
        els.favLayer.hidden = true;
        els.favoritesOpen.classList.remove("is-active");
        document.body.style.overflow = "";
        els.favoritesOpen.focus();
    }

    async function navigateBrowser(event) {
        event.preventDefault();
        const url = els.navInput.value.trim();
        if (!url) return;

        try {
            await api("/api/browser/navigate", {
                method: "POST",
                body: JSON.stringify({ url }),
            });
            els.navInput.value = "";
            toast("Страница отправлена в браузер");
            window.setTimeout(refreshState, 500);
        } catch (error) {
            toast(formatError(error));
            if (error.code === "auth") authLost();
        }
    }

    els.loginForm.addEventListener("submit", (event) => {
        event.preventDefault();
        void tryLogin();
    });

    els.search.addEventListener("input", () => {
        query = els.search.value;
        visibleLimit = PAGE_SIZE;
        renderPads(true);
    });

    els.category.addEventListener("change", () => {
        category = els.category.value;
        visibleLimit = PAGE_SIZE;
        renderPads(true);
    });

    els.loadMore.addEventListener("click", () => {
        visibleLimit += PAGE_SIZE;
        renderPads(true);
    });

    els.stopNow.addEventListener("click", () => void stopAll());
    els.stopAll.addEventListener("click", () => void stopAll());

    els.micButton.addEventListener("click", () => void toggleSetting("micPassthrough"));
    els.browserButton.addEventListener("click", () => void toggleSetting("browserSend"));
    els.monitorButton.addEventListener("click", () => void toggleSetting("monitorEnabled"));

    document.querySelectorAll("[data-setting]").forEach((input) => {
        input.addEventListener("change", () => {
            const key = input.dataset.setting;
            if (!state) return;
            state.settings[key] = input.checked;
            renderDock();
            void patchSettings({ [key]: input.checked });
        });
    });

    document.querySelectorAll("[data-gain]").forEach((input) => {
        const key = input.dataset.gain;
        const output = document.querySelector(`[data-output="${key}"]`);

        input.addEventListener("input", () => {
            const value = Number(input.value);
            if (output) output.textContent = value + "%";
            settingsDirtyUntil = Date.now() + 900;

            window.clearTimeout(rangeTimers.get(key));
            rangeTimers.set(key, window.setTimeout(() => {
                void patchSettings({ [key]: value });
            }, 180));
        });

        input.addEventListener("change", () => {
            window.clearTimeout(rangeTimers.get(key));
            void patchSettings({ [key]: Number(input.value) });
        });
    });

    els.settingsOpen.addEventListener("click", openSheet);
    els.sheetClose.addEventListener("click", closeSheet);
    els.sheetBackdrop.addEventListener("click", closeSheet);
    els.favoritesOpen.addEventListener("click", openFavorites);
    els.favClose.addEventListener("click", closeFavorites);
    els.favBackdrop.addEventListener("click", closeFavorites);
    els.navForm.addEventListener("submit", (event) => void navigateBrowser(event));

    document.addEventListener("keydown", (event) => {
        if (event.key !== "Escape") return;
        if (!els.favLayer.hidden) {
            closeFavorites();
            return;
        }
        closeSheet();
    });

    document.addEventListener("visibilitychange", () => {
        if (!document.hidden) refreshState();
    });

    window.setInterval(() => {
        if (!document.hidden) refreshState();
    }, 1200);

    if (token) {
        void refreshState();
    } else {
        showLogin();
    }
})();
