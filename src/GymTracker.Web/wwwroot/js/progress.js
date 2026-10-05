// Draws the Progress charts from the JSON the page embeds. Colours come from the design tokens, so the charts
// are redrawn when the theme changes. Series draw in the first time the page is seen in a session only.
window.gymModule("progress", () => {
    "use strict";
    if (!window.Chart) {
        return undefined;
    }

    const root = document.documentElement;
    const reduce = window.matchMedia("(prefers-reduced-motion: reduce)");
    const charts = [];

    function data(id) {
        const el = document.getElementById(id);
        return el ? JSON.parse(el.textContent) : null;
    }

    function token(name) {
        return getComputedStyle(root).getPropertyValue(name).trim();
    }

    function animateOnce() {
        if (reduce.matches) {
            return false;
        }
        try {
            const key = "gym.charts.seen";
            if (sessionStorage.getItem(key)) {
                return false;
            }
            sessionStorage.setItem(key, "1");
        } catch {
            return false;
        }
        return true;
    }

    function fade(hex, alpha) {
        // Accepts #rrggbb or rgb()/rgba(); returns rgba with the given alpha.
        if (hex.startsWith("#")) {
            const n = parseInt(hex.slice(1), 16);
            return `rgba(${(n >> 16) & 255}, ${(n >> 8) & 255}, ${n & 255}, ${alpha})`;
        }
        return hex;
    }

    function areaGradient(ctx, color) {
        const { chartArea } = ctx.chart;
        if (!chartArea) {
            return fade(color, 0.12);
        }
        const g = ctx.chart.ctx.createLinearGradient(0, chartArea.top, 0, chartArea.bottom);
        g.addColorStop(0, fade(color, 0.22));
        g.addColorStop(1, fade(color, 0));
        return g;
    }

    function draw(animate) {
        charts.splice(0).forEach((c) => c.destroy());

        const accent = token("--accent");
        const label = token("--label");
        const label2 = token("--label-2");
        const label3 = token("--label-3");
        const grid = token("--separator");
        const fill = token("--fill-strong");
        const surface = token("--surface-2");

        Chart.defaults.locale = "en-US"; // numbers in one format whatever the phone's region
        Chart.defaults.color = label2;
        Chart.defaults.font.family = getComputedStyle(document.body).fontFamily;
        Chart.defaults.font.size = 12;
        Chart.defaults.maintainAspectRatio = false;
        Chart.defaults.plugins.legend.display = false;
        Object.assign(Chart.defaults.plugins.tooltip, {
            backgroundColor: surface,
            titleColor: label2,
            bodyColor: label,
            borderColor: grid,
            borderWidth: 0.5,
            padding: 10,
            cornerRadius: 10,
            displayColors: false,
            titleFont: { weight: "400", size: 12 },
            bodyFont: { weight: "600", size: 14 },
            caretSize: 6,
        });
        Chart.defaults.elements.line.borderJoinStyle = "round";
        Chart.defaults.elements.line.borderCapStyle = "round";

        // No thousands separator, like the numbers in the rest of the app ("2253 kcal").
        const plain = (v) => Number(v).toLocaleString("en-US", { useGrouping: false, maximumFractionDigits: 2 });
        const xAxis = { grid: { display: false }, border: { display: false }, ticks: { maxTicksLimit: 5, maxRotation: 0, color: label3 } };
        const yAxis = (extra) => Object.assign({ position: "right", grid: { color: grid, drawTicks: false }, border: { display: false }, ticks: { maxTicksLimit: 4, padding: 8, color: label3, callback: plain } }, extra);
        const kg = (v) => t("{0} kg", v);
        const motion = animate ? { duration: 900, easing: "easeOutQuart" } : false;

        const weight = data("weight-data");
        if (weight && document.getElementById("weight-chart")) {
            charts.push(new Chart(document.getElementById("weight-chart"), {
                type: "line",
                data: {
                    labels: weight.labels,
                    datasets: [
                        { label: t("Trend"), data: weight.trend, borderColor: accent, borderWidth: 2.5, pointRadius: 0, pointHoverRadius: 4, tension: 0.35, fill: true, backgroundColor: (ctx) => areaGradient(ctx, accent), order: 1 },
                        { label: t("Weigh-in"), data: weight.raw, showLine: false, pointRadius: 2.5, pointHoverRadius: 4, pointBackgroundColor: label3, pointBorderWidth: 0, order: 2 },
                    ],
                },
                options: {
                    animation: motion,
                    interaction: { mode: "index", intersect: false },
                    plugins: { tooltip: { callbacks: { label: (c) => c.raw == null ? null : `${c.dataset.label}: ${kg(Number(c.raw).toFixed(1))}` } } },
                    scales: { x: xAxis, y: yAxis({ grace: "10%" }) },
                },
            }));
        }

        const e1rm = data("e1rm-data");
        if (e1rm && document.getElementById("e1rm-chart")) {
            charts.push(new Chart(document.getElementById("e1rm-chart"), {
                type: "line",
                data: {
                    labels: e1rm.labels,
                    datasets: [{ label: "e1RM", data: e1rm.values, borderColor: accent, borderWidth: 2.5, tension: 0.3, fill: true, backgroundColor: (ctx) => areaGradient(ctx, accent),
                        pointRadius: 3, pointBackgroundColor: accent, pointBorderWidth: 0, pointHoverRadius: 5 }],
                },
                options: {
                    animation: motion,
                    plugins: { tooltip: { callbacks: { label: (c) => kg(c.raw) } } },
                    scales: { x: xAxis, y: yAxis({ grace: "15%" }) },
                },
            }));
        }

        const calories = data("calories-data");
        if (calories && document.getElementById("calories-chart")) {
            charts.push(new Chart(document.getElementById("calories-chart"), {
                type: "bar",
                data: {
                    labels: calories.labels,
                    datasets: [
                        { type: "bar", label: t("Eaten"), data: calories.kcal, backgroundColor: fill, hoverBackgroundColor: label3, borderRadius: 4, borderSkipped: false, barPercentage: 0.7, order: 3 },
                        { type: "line", label: t("Target"), data: calories.target, borderColor: accent, borderWidth: 2, pointRadius: 0, stepped: true, order: 1 },
                        { type: "line", label: t("Maintenance"), data: calories.maintenance, borderColor: label2, borderWidth: 1.5, borderDash: [5, 4], pointRadius: 0, stepped: true, order: 2 },
                    ],
                },
                options: {
                    animation: motion,
                    interaction: { mode: "index", intersect: false },
                    plugins: { tooltip: { callbacks: { label: (c) => c.raw == null ? null : `${c.dataset.label}: ${t("{0} kcal", Math.round(c.raw))}` } } },
                    scales: { x: xAxis, y: yAxis({ beginAtZero: true }) },
                },
            }));
        }

        const protein = data("protein-data");
        if (protein && document.getElementById("protein-chart")) {
            charts.push(new Chart(document.getElementById("protein-chart"), {
                type: "bar",
                data: {
                    labels: protein.labels,
                    datasets: [
                        { type: "bar", label: t("Eaten"), data: protein.eaten, backgroundColor: fill, hoverBackgroundColor: label3, borderRadius: 4, borderSkipped: false, barPercentage: 0.7, order: 3 },
                        { type: "line", label: t("Aim"), data: protein.aim, borderColor: label, borderWidth: 2, pointRadius: 0, stepped: true, order: 1 },
                        { type: "line", label: t("Minimum"), data: protein.minimum, borderColor: label2, borderWidth: 1.5, borderDash: [5, 4], pointRadius: 0, order: 2 },
                    ],
                },
                options: {
                    animation: motion,
                    interaction: { mode: "index", intersect: false },
                    plugins: { tooltip: { callbacks: { label: (c) => c.raw == null ? null : `${c.dataset.label}: ${t("{0} g", Math.round(c.raw))}` } } },
                    scales: { x: xAxis, y: yAxis({ beginAtZero: true }) },
                },
            }));
        }

        const volume = data("volume-data");
        if (volume && document.getElementById("volume-chart")) {
            charts.push(new Chart(document.getElementById("volume-chart"), {
                type: "bar",
                data: {
                    labels: volume.labels,
                    datasets: [
                        { label: t("This week"), data: volume.thisWeek, backgroundColor: accent, borderRadius: 4, borderSkipped: false, barPercentage: 0.8, categoryPercentage: 0.7 },
                        { label: t("Last week"), data: volume.lastWeek, backgroundColor: fill, borderRadius: 4, borderSkipped: false, barPercentage: 0.8, categoryPercentage: 0.7 },
                    ],
                },
                options: {
                    animation: motion,
                    indexAxis: "y",
                    interaction: { mode: "index", intersect: false },
                    plugins: { tooltip: { callbacks: { label: (c) => `${c.dataset.label}: ${t("{0} sets", c.raw)}` } } },
                    scales: {
                        x: { position: "top", beginAtZero: true, grid: { color: grid, drawTicks: false }, border: { display: false }, ticks: { precision: 0, maxTicksLimit: 4, color: label3, padding: 6 } },
                        y: { grid: { display: false }, border: { display: false }, ticks: { color: label, font: { size: 13 } } },
                    },
                },
            }));
        }
    }

    draw(animateOnce());

    // Redraw with the new colours when the theme or the phone's appearance changes.
    const redraw = () => draw(false);
    const themeObserver = new MutationObserver(redraw);
    themeObserver.observe(root, { attributes: true, attributeFilter: ["data-theme"] });
    const scheme = window.matchMedia("(prefers-color-scheme: dark)");
    scheme.addEventListener("change", redraw);

    return () => {
        themeObserver.disconnect();
        scheme.removeEventListener("change", redraw);
        charts.splice(0).forEach((c) => c.destroy());
    };
});
