# Design samples (dev only)

Three static directions for the redesign. Not part of the app: nothing here is served, precached or copied into the Docker image.

- `a-fitness.html`, `b-mono.html`, `c-apple.html`: open directly in a browser. Query parameters: `theme=light|dark`, `screen=today|session`, `timer=off`, `sheet=open`.
- `src/`: the sources. `tools/build.mjs` inlines the shared motion CSS, icons, session markup and behaviour so each sample is one file.
- `contract.md`: selectors, ids and data attributes that the app's JavaScript, htmx and tests depend on.
- `tools/seed-demo.sql`: demo data for the local dev database; `tools/screens.mjs` screenshots every app screen.

Tools need `playwright-core` and Google Chrome:

```sh
node design-samples/tools/build.mjs
node design-samples/tools/sample-shots.mjs [--video]    # screenshots/ and screenshots/motion/*.webm
node design-samples/tools/motion-frames.mjs             # screenshots/motion/*.png, animations slowed 10×
node design-samples/tools/motion-record.mjs <outDir>    # the running app: tour video, slowed frame strips and animation timelines
node design-samples/tools/check.mjs                     # clicks through each sample, reports script errors
```
