---
version: alpha
name: Screenline
description: A native Windows screenshot clothesline with translucent photo frames and quiet slate-blue controls.
colors:
  primary: "#24465D"
  foreground: "#1F3340"
  muted: "#59717F"
  surface: "#EDF4F7"
  border: "#D7E3E9"
  rope: "#8094A0"
  white: "#FFFFFF"
typography:
  body:
    fontFamily: "Segoe UI, Microsoft YaHei UI, sans-serif"
    fontSize: "14px"
  caption:
    fontFamily: "Segoe UI, Microsoft YaHei UI, sans-serif"
    fontSize: "11px"
rounded:
  DEFAULT: "7px"
  card: "8px"
  onboarding: "13px"
spacing:
  control-gap: "8px"
  dialog-padding: "28px"
components:
  button:
    rounded: "7px"
    backgroundColor: "{colors.primary}"
    textColor: "{colors.white}"
  photo:
    rounded: "8px"
    backgroundColor: "{colors.white}"
    textColor: "{colors.foreground}"
  onboarding:
    rounded: "13px"
    backgroundColor: "{colors.white}"
    textColor: "{colors.muted}"
  rope:
    backgroundColor: "{colors.rope}"
  border:
    backgroundColor: "{colors.border}"
---

# Screenline design

## Overview

Native desktop utility for a Chinese-speaking Windows user who wants to keep screenshots temporarily within reach. The reference is the hanging-photo interaction of Tendedero, not its website, name, icon, or artwork. The one expressive feature is a slightly curved rope with independently tilted white photo frames and small wooden pegs. Settings and editing use familiar Windows controls with restrained slate blue accents.

The product has a Chinese UI with English Screenline branding only. It is a local desktop tool, without accounts, networking, or a web dashboard. Do not introduce a marketing page, sidebar, hero, or card grid.

Runtime tokens in `src/Theme.cs` are canonical (model B). This document mirrors their exact values. Primary → Theme.Accent; foreground → Theme.Foreground; muted → Theme.Muted; surface → Theme.Surface; border → Theme.Border; rope → Theme.Rope; body/caption → Theme.Font. Shared Theme.Button/ActionButton implement the control states. New UI must consume these owners.

## Colors

The cool pale surface separates text from the variable desktop wallpaper. Opaque white photo frames keep screenshots recognizable against both light and dark backgrounds. Rope is neutral enough to recede. Foreground and muted text both retain readable contrast on pale surfaces; control focus uses the primary slate blue. Native Windows scrollbars, dialogs and menus retain their system theme and high contrast behavior. The custom surface currently has one light theme.

## Typography

Segoe UI with Microsoft YaHei UI fallback covers Latin and simplified Chinese. Use 12–14 DIP for utility controls, 11 DIP for interaction hints, 9–10 DIP for optional photo metadata, and 24 DIP for the settings heading. Image captions truncate with a tooltip containing their complete name. No decorative font is needed on this utility surface.

## Layout

The shelf is a 278 DIP transparent top-of-monitor window. The rope spans the monitor; photos scroll horizontally within its own native ScrollViewer. Photo cards reserve 182 DIP width and a 104 DIP image stage. The desktop remains visible around the cards. Empty state offers screenshot and file import. Settings scroll internally and pin Save/Cancel below the scroll region. The editor wraps its toolbar as the window narrows. At least 640 DIP desktop width is assumed.

## Elevation & Depth

Only the small photo frames and onboarding surface cast shadows. Transparent parts of the shelf pass pointer input to the desktop; no full-width scrim or opaque dashboard panel. The screenshot-selection window intentionally covers the desktop only while selecting a region.

## Shapes

Buttons use 7 DIP corners; white photo frames use 8 DIP; onboarding uses 13 DIP. Wooden pegs are narrow vertical rectangles with one metal spring line. The rope curve, slight card rotations and photo shadows carry the physical metaphor.

## Components

Theme.Button owns hover, pressed, disabled and visible keyboard focus states without resizing. Standard WPF controls own checkboxes, menus, scrollbars, file/folder dialogs and window chrome. Buttons expose Chinese AutomationProperties names. Save/remove actions remain visible below each image and have tooltips.

ShelfWindow.SetStatus owns short feedback at a stable lower-center position. Settings uses inline feedback. Native dialog ownership is intentional for Windows file picking and saving; no browser dialog is involved. See UX-CONTRACT.md for action semantics.

The shelf fades in over 150ms and cards straighten slightly on hover. `SystemParameters.ClientAreaAnimation` disables custom motion when requested by Windows. No continuous decorative animation. The preview uses full image dimensions for annotated export, with a Viewbox scaling only its display.

## Do's and Don'ts

- Keep screenshots and the desktop visually dominant.
- Preserve the same Chinese verbs and source-safe file behavior across tray, card and keyboard actions.
- Do not reuse upstream protected branding or documentation artwork.
- Do not turn background import into a focus-stealing window.
