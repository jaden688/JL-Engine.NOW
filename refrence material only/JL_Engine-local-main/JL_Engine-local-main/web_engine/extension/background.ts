// extension/background.ts
// MV3 service worker. One job for now: clicking the toolbar icon opens the
// operator side panel. Later this is where long-lived browser missions,
// alarms, and cross-tab orchestration live.

chrome.sidePanel
  .setPanelBehavior({ openPanelOnActionClick: true })
  .catch((err: unknown) => console.error("[jl-engine] side panel behavior failed", err));
