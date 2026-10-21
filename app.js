(() => {
    "use strict";

    const reduceMotion = window.matchMedia("(prefers-reduced-motion: reduce)");

    /* Mobile navigation */
    const nav = document.getElementById("nav");
    const menuButton = document.querySelector(".menu-toggle");

    if (nav && menuButton) {
        const closeNav = () => {
            nav.classList.remove("is-open");
            menuButton.setAttribute("aria-expanded", "false");
        };

        menuButton.addEventListener("click", () => {
            const open = nav.classList.toggle("is-open");
            menuButton.setAttribute("aria-expanded", String(open));
        });

        nav.querySelectorAll("a").forEach((link) => link.addEventListener("click", closeNav));

        document.addEventListener("keydown", (event) => {
            if (event.key === "Escape") closeNav();
        });
    }

    /* Screenshot tabs */
    const shots = {
        sounds: {
            number: "01",
            title: "Библиотека и микшер",
            text: "Пады с цветами, поиск, избранное и три канала MIC / PAD / WEB. Мастер-лимитер держит выход в узде.",
            meta: "48 kHz · stereo · limiter",
            image: "assets/sounds.png",
            alt: "Библиотека звуков FreeSoundPad: пады, поиск, категории и микшер",
        },
        browser: {
            number: "02",
            title: "Встроенный браузер",
            text: "YouTube, Twitch, SoundCloud и Spotify внутри приложения. Звук вкладки захватывается отдельно от системы.",
            meta: "WebView2 · process loopback",
            image: "assets/browser.png",
            alt: "Встроенный браузер FreeSoundPad со стартовой страницей и быстрыми ссылками",
        },
        settings: {
            number: "03",
            title: "Маршрутизация и захват",
            text: "Выход на виртуальный кабель, мониторинг в наушники, уровень мониторинга и проверка виртуального кабеля.",
            meta: "CABLE Input / Output",
            image: "assets/settings.png",
            alt: "Настройки FreeSoundPad: аудиоустройства, виртуальный кабель и громкость мониторинга",
        },
    };

    const tabs = Array.from(document.querySelectorAll("[data-shot]"));
    const image = document.getElementById("shotImage");
    const number = document.getElementById("shotNumber");
    const title = document.getElementById("shotTitle");
    const text = document.getElementById("shotText");
    const meta = document.getElementById("shotMeta");

    const selectShot = (key) => {
        const shot = shots[key];
        if (!shot || !image) return;

        tabs.forEach((tab) => {
            const active = tab.dataset.shot === key;
            tab.classList.toggle("is-active", active);
            tab.setAttribute("aria-selected", String(active));
        });

        image.classList.add("is-switching");
        window.setTimeout(() => {
            image.src = shot.image;
            image.alt = shot.alt;
            image.classList.remove("is-switching");
        }, reduceMotion.matches ? 0 : 150);

        if (number) number.textContent = shot.number;
        if (title) title.textContent = shot.title;
        if (text) text.textContent = shot.text;
        if (meta) meta.textContent = shot.meta;
    };

    tabs.forEach((tab, index) => {
        tab.addEventListener("click", () => selectShot(tab.dataset.shot));
        tab.addEventListener("keydown", (event) => {
            if (event.key !== "ArrowLeft" && event.key !== "ArrowRight") return;
            event.preventDefault();
            const direction = event.key === "ArrowRight" ? 1 : -1;
            const next = tabs[(index + direction + tabs.length) % tabs.length];
            next.focus();
            selectShot(next.dataset.shot);
        });
    });

    /* Signal waveform */
    const canvas = document.getElementById("signalCanvas");
    const rail = canvas?.closest(".signal-rail");

    if (canvas && rail) {
        const ctx = canvas.getContext("2d");
        let width = 0;
        let height = 0;
        let animationFrame = 0;

        const resize = () => {
            const rect = rail.getBoundingClientRect();
            const ratio = Math.min(window.devicePixelRatio || 1, 2);
            width = Math.max(1, Math.floor(rect.width));
            height = Math.max(1, Math.floor(rect.height));
            canvas.width = Math.floor(width * ratio);
            canvas.height = Math.floor(height * ratio);
            canvas.style.width = width + "px";
            canvas.style.height = height + "px";
            ctx.setTransform(ratio, 0, 0, ratio, 0, 0);
            draw(0);
        };

        const draw = (time) => {
            if (!ctx) return;
            ctx.clearRect(0, 0, width, height);

            const center = height / 2;
            ctx.strokeStyle = "rgba(205, 226, 250, .08)";
            ctx.lineWidth = 1;
            ctx.beginPath();
            ctx.moveTo(0, center);
            ctx.lineTo(width, center);
            ctx.stroke();

            const waves = [
                { color: "rgba(72, 224, 174, .46)", amp: height * 0.23, speed: 0.00062, freq: 0.012, phase: 0.8 },
                { color: "rgba(141, 125, 255, .22)", amp: height * 0.14, speed: -0.00043, freq: 0.018, phase: 2.1 },
            ];

            waves.forEach((wave) => {
                ctx.beginPath();
                for (let x = 0; x <= width; x += 2) {
                    const envelope = Math.sin((x / width) * Math.PI);
                    const local = Math.sin(x * wave.freq + time * wave.speed + wave.phase);
                    const detail = Math.sin(x * wave.freq * 2.7 - time * wave.speed * 1.8) * 0.18;
                    const y = center + (local + detail) * wave.amp * envelope;
                    if (x === 0) ctx.moveTo(x, y);
                    else ctx.lineTo(x, y);
                }
                ctx.strokeStyle = wave.color;
                ctx.lineWidth = 1.25;
                ctx.stroke();
            });
        };

        const animate = (now) => {
            draw(now);
            animationFrame = window.requestAnimationFrame(animate);
        };

        const start = () => {
            resize();
            window.cancelAnimationFrame(animationFrame);
            if (reduceMotion.matches) draw(0);
            else animationFrame = window.requestAnimationFrame(animate);
        };

        if ("ResizeObserver" in window) {
            new ResizeObserver(resize).observe(rail);
        } else {
            window.addEventListener("resize", resize);
        }

        reduceMotion.addEventListener?.("change", start);
        start();
    }
})();
