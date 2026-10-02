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
