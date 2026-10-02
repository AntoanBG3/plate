// Draws the Progress charts from the JSON the page embeds.
(function () {
    "use strict";
    if (!window.Chart) {
        return;
    }

    const lime = "#c8f031";
    const sky = "#9be0ff";
    const muted = "#a9b3a6";
    const grid = "rgba(255, 255, 255, 0.08)";

    Chart.defaults.color = muted;
    Chart.defaults.font.family = '"Fira Sans", sans-serif';
    Chart.defaults.animation = false;
    Chart.defaults.maintainAspectRatio = false;

    function data(id) {
        const el = document.getElementById(id);
        return el ? JSON.parse(el.textContent) : null;
    }

    const e1rm = data("e1rm-data");
    if (e1rm) {
        new Chart(document.getElementById("e1rm-chart"), {
            type: "line",
            data: {
                labels: e1rm.labels,
                datasets: [{
                    label: "e1RM (kg)",
                    data: e1rm.values,
                    borderColor: lime,
                    backgroundColor: lime,
                    borderWidth: 2.5,
                    pointRadius: 4,
                    tension: 0.25,
                }],
            },
            options: {
                plugins: { legend: { display: false } },
                scales: {
                    x: { grid: { color: grid } },
                    y: { grid: { color: grid }, ticks: { callback: (v) => `${v} kg` } },
                },
            },
        });
    }

    const amber = "#ffcf8a";

    const weight = data("weight-data");
    if (weight) {
        new Chart(document.getElementById("weight-chart"), {
            type: "line",
            data: {
                labels: weight.labels,
                datasets: [
                    {
                        label: "Weigh-in",
                        data: weight.raw,
                        showLine: false,
                        pointRadius: 3,
                        backgroundColor: sky,
                        borderColor: sky,
                    },
                    {
                        label: "Trend",
                        data: weight.trend,
                        borderColor: lime,
                        borderWidth: 2.5,
                        pointRadius: 0,
                        tension: 0.2,
                    },
                ],
            },
            options: {
                plugins: { legend: { display: false } },
                scales: {
                    x: { grid: { color: grid }, ticks: { maxTicksLimit: 6, maxRotation: 0 } },
                    y: { grid: { color: grid }, ticks: { callback: (v) => `${v} kg` } },
                },
            },
        });
    }

    const calories = data("calories-data");
    if (calories) {
        new Chart(document.getElementById("calories-chart"), {
            type: "bar",
            data: {
                labels: calories.labels,
                datasets: [
                    { type: "bar", label: "Eaten", data: calories.kcal, backgroundColor: "rgba(155, 224, 255, 0.55)", borderRadius: 4, order: 3 },
                    { type: "line", label: "Target", data: calories.target, borderColor: lime, borderWidth: 2.5, pointRadius: 0, stepped: true, order: 1 },
                    { type: "line", label: "Maintenance", data: calories.maintenance, borderColor: amber, borderWidth: 1.5, borderDash: [6, 4], pointRadius: 0, stepped: true, order: 2 },
                ],
            },
            options: {
                plugins: { legend: { position: "top", align: "start" } },
                scales: {
                    x: { grid: { display: false }, ticks: { maxTicksLimit: 6, maxRotation: 0 } },
                    y: { grid: { color: grid }, beginAtZero: true },
                },
            },
        });
    }

    const volume = data("volume-data");
    if (volume) {
        new Chart(document.getElementById("volume-chart"), {
            type: "bar",
            data: {
                labels: volume.labels,
                datasets: [
                    { label: "This week", data: volume.thisWeek, backgroundColor: lime, borderRadius: 6 },
                    { label: "Last week", data: volume.lastWeek, backgroundColor: sky, borderRadius: 6 },
                ],
            },
            options: {
                indexAxis: "y",
                plugins: { legend: { position: "top", align: "start" } },
                scales: {
                    x: { grid: { color: grid }, beginAtZero: true, ticks: { precision: 0 } },
                    y: { grid: { display: false } },
                },
            },
        });
    }
})();
