// The in-progress workout shown by every sample: same content, each sample styles it.
const check = '<svg viewBox="0 0 24 24" aria-hidden="true"><path class="check" pathLength="1" d="M5.5 12.5l4.25 4.25L18.5 7.5"/></svg>';
const exercises = [
    { name: "Incline dumbbell press", meta: "8–12 reps · 3 sets · rest 2:30", rest: 150, why: "All sets hit 12, +2.5 kg",
      warmups: [[14, 10], [22, 6]], sets: [[32.5, 10, true], [32.5, 9, true], [32.5, 9, false]] },
    { name: "Seated cable row", meta: "8–12 reps · 3 sets · rest 2:00", rest: 120, why: "Hold 62.5 kg, add a rep",
      warmups: [], sets: [[62.5, 11, true], [62.5, 11, false], [62.5, 10, false]] },
    { name: "Lateral raise", meta: "12–20 reps · 3 sets · rest 1:00", rest: 60, why: "Hold 10 kg, add a rep",
      warmups: [], sets: [[10, 16, false], [10, 15, false], [10, 14, false]] },
];

function row(n, kg, reps, done, warmup) {
    const cls = [warmup && "is-warmup", done && "is-done"].filter(Boolean).join(" ");
    const val = (v, label) => done
        ? `value="${v}" aria-label="${label}"`
        : `placeholder="${v}" data-suggested aria-label="${label}, suggested ${v}"`;
    return `
          <div class="set${cls ? " " + cls : ""}" data-set>
            <button class="set-delete" data-delete-set>Delete</button>
            <div class="set-content"><span class="set-n"${warmup ? "" : " data-n"}>${warmup ? "W" : n}</span>
              <label class="field"><input class="num" inputmode="decimal" ${val(kg, warmup ? "Warm-up weight, kg" : "Weight, kg")}><small>kg</small></label>
              <label class="field"><input class="num reps" inputmode="numeric" ${val(reps, "Reps")}><small>reps</small></label>
              <button class="done-btn" data-done aria-pressed="${done}" aria-label="${done ? "Done" : "Log set"}"><span class="disc">${check}</span></button>
            </div>
          </div>`;
}

export function session() {
    return exercises.map((e) => `
      <article class="exercise" data-exercise data-rest="${e.rest}">
        <div class="ex-head">
          <div>
            <h2 class="ex-name">${e.name}</h2>
            <p class="ex-meta">${e.meta}</p>
          </div>
          <div class="menu-wrap">
            <button class="icon-btn" data-menu-trigger aria-label="Actions for ${e.name}" aria-expanded="false"><svg class="icon"><use href="#i-more"/></svg></button>
            <div class="menu" role="menu">
              <button role="menuitem">Move up <svg class="icon"><use href="#i-up"/></svg></button>
              <button role="menuitem">Move down <svg class="icon"><use href="#i-down"/></svg></button>
              <button role="menuitem" class="danger">Remove exercise <svg class="icon"><use href="#i-trash"/></svg></button>
            </div>
          </div>
        </div>
        <div class="ex-body">
          <p class="suggestion"><svg class="icon"><use href="#i-trend"/></svg>${e.why}</p>
          <div data-rows>${e.warmups.map(([kg, r]) => row(0, kg, r, true, true)).join("")}${e.sets.map(([kg, r, d], i) => row(i + 1, kg, r, d, false)).join("")}
          </div>
          <div class="ex-actions">
            <button class="plain-btn" data-add-set><svg class="icon"><use href="#i-plus"/></svg>Add set</button>
            <button class="plain-btn"><svg class="icon"><use href="#i-plus"/></svg>Warm-up</button>
          </div>
        </div>
      </article>`).join("\n");
}
