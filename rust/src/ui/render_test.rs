//! Headless software rendering; never creates an OS window or reads credentials.
use std::{cell::RefCell, path::Path, rc::Rc};
use chrono::{DateTime, Local, Utc};
use slint::{Model, ModelRc, Rgb8Pixel, VecModel};
use slint::platform::{
    Platform, WindowAdapter,
    software_renderer::{MinimalSoftwareWindow, RepaintBufferType},
};
use crate::core::{
    contracts::{Snapshot, SnapshotStore, kind, state},
    format,
};
use crate::engine::stores::FileSnapshotStore;
use super::{CardData, ChipData, Chips, Flyout, HeaderButtonData, LineData, chip_model, skins};

fn color(rgb: u32) -> slint::Color {
    slint::Color::from_rgb_u8((rgb >> 16) as u8, (rgb >> 8) as u8, rgb as u8)
}

fn rgba(r: u8, g: u8, b: u8, a: u8) -> slint::Color {
    slint::Color::from_argb_u8(a, r, g, b)
}

fn lighten(rgb: u32, amount: f32) -> slint::Color {
    let r = ((rgb >> 16) & 0xff) as f32;
    let g = ((rgb >> 8) & 0xff) as f32;
    let b = (rgb & 0xff) as f32;
    let nr = (r + (255.0 - r) * amount).clamp(0.0, 255.0) as u8;
    let ng = (g + (255.0 - g) * amount).clamp(0.0, 255.0) as u8;
    let nb = (b + (255.0 - b) * amount).clamp(0.0, 255.0) as u8;
    slint::Color::from_rgb_u8(nr, ng, nb)
}

fn darken(rgb: u32, amount: f32) -> slint::Color {
    let r = ((rgb >> 16) & 0xff) as f32;
    let g = ((rgb >> 8) & 0xff) as f32;
    let b = (rgb & 0xff) as f32;
    let nr = (r * (1.0 - amount)).clamp(0.0, 255.0) as u8;
    let ng = (g * (1.0 - amount)).clamp(0.0, 255.0) as u8;
    let nb = (b * (1.0 - amount)).clamp(0.0, 255.0) as u8;
    slint::Color::from_rgb_u8(nr, ng, nb)
}

fn brand_classic(id: &str) -> (&'static str, u32) {
    match id {
        "claude" => ("C", 0xe07a5f),
        "codex" => ("X", 0x60a5fa),
        "gemini" => ("G", 0x7c9cff),
        "grok" => ("K", 0xd6d3d1),
        _ => ("?", 0x9ca3af),
    }
}

fn brand_fluent(id: &str) -> (&'static str, u32, u32) {
    match id {
        "claude" => ("C", 0xea580c, 0xffffff),
        "codex" => ("X", 0x3b82f6, 0xffffff),
        "gemini" => ("G", 0x818cf8, 0xffffff),
        "grok" => ("K", 0xcbd5e1, 0x141418),
        _ => ("?", 0x9aa0aa, 0xffffff),
    }
}

fn brand_terminal(id: &str) -> (&'static str, u32) {
    match id {
        "claude" => ("C", 0xff8555),
        "codex" => ("X", 0x50c8ff),
        "gemini" => ("G", 0x8ca4ff),
        "grok" => ("K", 0xd2dcd7),
        _ => ("?", 0x6eb482),
    }
}

fn dashed_round_outline(width: f32, h: f32, r: f32) -> String {
    use std::f32::consts::{FRAC_PI_2, PI};
    let a = width - 2.0 * r - 1.0;
    let b = FRAC_PI_2 * r;
    let c = h - 2.0 * r - 1.0;
    let spans = [a, b, c, b, a, b, c, b];
    let point = |mut distance: f32| {
        let mut side = 7;
        for (i, span) in spans.iter().enumerate() {
            side = i;
            if distance <= *span {
                break;
            }
            distance -= span;
        }
        match side {
            0 => (r + 0.5 + distance, 0.5),
            1 => {
                let angle = -FRAC_PI_2 + distance / r;
                (width - r - 0.5 + r * angle.cos(), r + 0.5 + r * angle.sin())
            }
            2 => (width - 0.5, r + 0.5 + distance),
            3 => {
                let angle = distance / r;
                (width - r - 0.5 + r * angle.cos(), h - r - 0.5 + r * angle.sin())
            }
            4 => (width - r - 0.5 - distance, h - 0.5),
            5 => {
                let angle = FRAC_PI_2 + distance / r;
                (r + 0.5 + r * angle.cos(), h - r - 0.5 + r * angle.sin())
            }
            6 => (0.5, h - r - 0.5 - distance),
            _ => {
                let angle = PI + distance / r;
                (r + 0.5 + r * angle.cos(), r + 0.5 + r * angle.sin())
            }
        }
    };
    let perimeter: f32 = spans.iter().sum();
    let mut path = String::new();
    let mut d = 0.0;
    use std::fmt::Write;
    while d < perimeter {
        if (d as i32 / 3) % 2 == 0 {
            let (x, y) = point(d);
            let (nx, ny) = point((d + 1.0).min(perimeter));
            let _ = write!(path, "M{x:.2} {y:.2} L{nx:.2} {ny:.2} ");
        }
        d += 1.0;
    }
    path
}

fn dashed_outline(width: f32) -> String {
    dashed_round_outline(width, 22.0, 5.0)
}

fn dashed_rect_outline(width: f32, height: f32) -> String {
    use std::fmt::Write;
    let mut path = String::new();
    let mut x = 0.5;
    while x < width - 0.5 {
        let nx = (x + 3.0).min(width - 0.5);
        let _ = write!(path, "M{x:.1} 0.5 L{nx:.1} 0.5 ");
        let _ = write!(path, "M{x:.1} {:.1} L{nx:.1} {:.1} ", height - 0.5, height - 0.5);
        x += 6.0;
    }
    let mut y = 0.5;
    while y < height - 0.5 {
        let ny = (y + 3.0).min(height - 0.5);
        let _ = write!(path, "M0.5 {y:.1} L0.5 {ny:.1} ");
        let _ = write!(path, "M{:.1} {y:.1} L{:.1} {ny:.1} ", width - 0.5, width - 0.5);
        y += 6.0;
    }
    path
}

pub fn text_width_font(text: &str, family: &str, px: i32, bold: bool) -> i32 {
    if text.is_empty() {
        return 0;
    }
    #[cfg(windows)]
    unsafe {
        use windows_sys::Win32::{Foundation::SIZE, Graphics::Gdi::*};
        let dc = CreateCompatibleDC(std::ptr::null_mut());
        let mut face: Vec<u16> = family.encode_utf16().collect();
        face.push(0);
        let weight = if bold { 700 } else { 400 };
        let font = CreateFontW(
            -px,
            0,
            0,
            0,
            weight,
            0,
            0,
            0,
            1,
            0,
            0,
            0,
            0,
            face.as_ptr(),
        );
        let old = SelectObject(dc, font);
        let wide: Vec<_> = text.encode_utf16().collect();
        let mut size: SIZE = std::mem::zeroed();
        GetTextExtentPoint32W(dc, wide.as_ptr(), wide.len() as i32, &mut size);
        SelectObject(dc, old);
        DeleteObject(font);
        DeleteDC(dc);
        size.cx + 7
    }
    #[cfg(not(windows))]
    {
        let _ = (family, bold);
        (text.len() as i32 * px * 6) / 10
    }
}

// GDI measures Segoe UI exactly as Theme.TextWidth (NoPadding), without a window.
pub fn text_width(text: &str, px: i32, bold: bool) -> i32 {
    text_width_font(text, "Segoe UI", px, bold)
}

fn capitalize_first(s: &str) -> String {
    let mut chars = s.chars();
    match chars.next() {
        None => String::new(),
        Some(first) => first.to_uppercase().collect::<String>() + chars.as_str(),
    }
}

fn update_classic(chips: &Chips, flyout: &Flyout, snapshot: Option<&Snapshot>, now: DateTime<Utc>) {
    let mut x = 7;
    let mut chip_data = Vec::new();
    if let Some(s) = snapshot {
        for p in &s.providers {
            let c = chip_model::for_provider(Some(p));
            let (initial, accent) = brand_classic(&p.id);
            let w = 31 + text_width(&c.text, 11, true);
            let (ink, outline) = match c.color_class {
                "red" => (0xfecaca, 0xef4444),
                "green" => (0xbbf7d0, 0x22c55e),
                _ => (0x9ca3af, 0x6b7280),
            };
            chip_data.push(ChipData {
                id: p.id.clone().into(),
                text: c.text.into(),
                initial: initial.into(),
                accent: color(accent),
                ink: color(ink),
                outline: color(outline),
                stale: c.stale,
                px: x as f32,
                pw: w as f32,
                dash_path: if c.stale {
                    dashed_outline(w as f32).into()
                } else {
                    Default::default()
                },
                ..Default::default()
            });
            x += w + 6;
        }
    }
    if chip_data.is_empty() {
        let w = 31 + text_width("loading\u{2026}", 11, true);
        chip_data.push(ChipData {
            text: "loading\u{2026}".into(),
            initial: "?".into(),
            ink: color(0x9ca3af),
            outline: color(0x6b7280),
            accent: color(0x9ca3af),
            px: 7.0,
            pw: w as f32,
            ..Default::default()
        });
        x = 7 + w + 6;
    }
    let width = (x + 1).clamp(96, 720) as f32;
    let model: ModelRc<ChipData> = Rc::new(VecModel::from(chip_data)).into();
    if chips.get_chips().iter().collect::<Vec<_>>() != model.iter().collect::<Vec<_>>() {
        chips.set_chips(model);
    }
    chips.set_content_width(width);
    chips.set_content_height(30.0);

    let mut cards = Vec::new();
    let mut heights = Vec::new();
    if let Some(s) = snapshot {
        for (index, p) in s.providers.iter().enumerate() {
            let (initial, accent) = brand_classic(&p.id);
            let mut lines = Vec::new();
            let mut y = 30.0;
            let mut label_width = 28;
            let mut add = |mut line: LineData| {
                line.py = y;
                y += if line.kind == 0 { 22.0 } else { 20.0 };
                lines.push(line);
            };
            if p.status.state == state::STALE {
                add(LineData {
                    kind: 4,
                    text: format::stale_text(p, now, &Local).into(),
                    ..Default::default()
                });
            }
            for w in &p.windows {
                if w.kind == kind::CREDITS && w.used_pct.is_none() {
                    add(LineData {
                        kind: 1,
                        text: w.label.clone().into(),
                        ..Default::default()
                    });
                    continue;
                }
                let label = format::flyout_label(w);
                label_width = label_width.max(text_width(&label, 11, true) + 2);
                add(LineData {
                    kind: 0,
                    label: label.into(),
                    pct: format::format_used_total(Some(w), false).into(),
                    eta: format::format_eta(w.resets_at, now, &Local).into(),
                    fill: w.used_pct.unwrap_or(0.0).clamp(0.0, 100.0) as f32,
                    alert: crate::core::models::is_alerting(w),
                    ..Default::default()
                });
            }
            for note in p.footnotes.iter().filter(|n| !n.is_empty()) {
                add(LineData {
                    kind: 3,
                    text: note.clone().into(),
                    ..Default::default()
                });
            }
            drop(add);
            if lines.is_empty() {
                if let Some(hint) = format::status_text(p) {
                    lines.push(LineData {
                        kind: 2,
                        text: hint.into(),
                        py: y,
                        ..Default::default()
                    });
                    y += 20.0;
                }
            }
            let h = if lines.is_empty() { 39.0 } else { y - 6.0 + 9.0 };
            heights.push(h);
            for line in &mut lines {
                line.pct_width = text_width(line.pct.as_str(), 11, true).min(46) as f32;
            }
            cards.push(CardData {
                id: p.id.clone().into(),
                name: p.display_name.as_ref().unwrap_or(&p.id).clone().into(),
                plan: p.plan.clone().unwrap_or_default().into(),
                initial: initial.into(),
                accent: color(accent),
                lines: Rc::new(VecModel::from(lines)).into(),
                px: if index % 2 == 0 { 13.0 } else { 324.0 },
                py: 0.0,
                ph: 0.0,
                label_width: label_width.min(104) as f32,
                eta_width: 46.0,
                ..Default::default()
            });
        }
    }
    let eta_width = cards
        .iter()
        .flat_map(|c| c.lines.iter())
        .filter(|l| l.kind == 0 && !l.eta.is_empty())
        .map(|l| text_width(l.eta.as_str(), 11, false) + 2)
        .max()
        .unwrap_or(46)
        .max(46) as f32;
    for c in &mut cards {
        c.eta_width = eta_width;
        c.label_width = c.label_width.min(279.0 - 46.0 - eta_width - 48.0);
        if c.label_width < 28.0 {
            c.label_width = 69.0;
        }
    }
    let mut y = 49.0;
    for row in (0..cards.len()).step_by(2) {
        let h = heights[row].max(*heights.get(row + 1).unwrap_or(&40.0));
        for c in cards.iter_mut().skip(row).take(2) {
            c.py = y;
            c.ph = h;
        }
        y += h + 8.0;
    }
    flyout.set_content_width(640.0);
    flyout.set_content_height(if cards.is_empty() { 120.0 } else { y + 3.0 });
    let key = format!(
        "classic|{}",
        cards
            .iter()
            .map(|c| format!(
                "{:?}",
                (
                    &c.id,
                    &c.name,
                    &c.plan,
                    &c.initial,
                    c.accent,
                    c.px,
                    c.py,
                    c.ph,
                    c.label_width,
                    c.eta_width,
                    c.lines.iter().collect::<Vec<_>>()
                )
            ))
            .collect::<Vec<_>>()
            .join("|")
    );
    if flyout.get_render_key().as_str() != key {
        flyout.set_render_key(key.into());
        flyout.set_cards(Rc::new(VecModel::from(cards)).into());
    }
}

fn update_rpg(chips: &Chips, flyout: &Flyout, snapshot: Option<&Snapshot>, now: DateTime<Utc>) {
    let mut x = 8;
    let mut chip_data = Vec::new();
    if let Some(s) = snapshot {
        for p in &s.providers {
            let c = chip_model::rpg_for_provider(Some(p));
            let (initial, accent) = brand_classic(&p.id);
            let is_ko = c.text == "\u{2014}";
            let display_text = if is_ko {
                "KO".to_string()
            } else {
                c.text.clone()
            };
            let w = 32 + text_width_font(&display_text, "Consolas", 11, true);
            let (bg_top, bg_bottom, outline, badge_border, ink) = if is_ko {
                (0x18121c, 0x0a080e, 0x8296b9, 0xef4444, 0xff5a5a)
            } else if c.stale {
                (0x261c0c, 0x120c06, 0xf59e0b, 0xffd700, 0xffd228)
            } else if c.color_class == "red" {
                (0x28121a, 0x12080c, 0xef4444, 0xffd700, 0xfca5a5)
            } else {
                (0x122660, 0x060c26, 0x248c50, 0xffd700, 0x86efac)
            };
            let stale_dash = c.stale && !is_ko;
            chip_data.push(ChipData {
                id: p.id.clone().into(),
                text: display_text.into(),
                initial: initial.into(),
                accent: color(accent),
                ink: color(ink),
                outline: color(outline),
                stale: stale_dash,
                px: x as f32,
                pw: w as f32,
                dash_path: if stale_dash {
                    dashed_round_outline(w as f32, 20.0, 3.0).into()
                } else {
                    Default::default()
                },
                bg_top: color(bg_top),
                bg_bottom: color(bg_bottom),
                badge_border: color(badge_border),
                ..Default::default()
            });
            x += w + 8;
        }
    }
    if chip_data.is_empty() {
        let w = 32 + text_width_font("loading\u{2026}", "Consolas", 11, true);
        chip_data.push(ChipData {
            text: "loading\u{2026}".into(),
            initial: "?".into(),
            accent: color(0xb9cdeb),
            ink: color(0xa0afc3),
            outline: color(0x8296b9),
            bg_top: color(0x122660),
            bg_bottom: color(0x060c26),
            badge_border: color(0xffd700),
            px: 8.0,
            pw: w as f32,
            ..Default::default()
        });
        x = 8 + w + 8;
    }
    let width = x.clamp(96, 760) as f32;
    let model: ModelRc<ChipData> = Rc::new(VecModel::from(chip_data)).into();
    if chips.get_chips().iter().collect::<Vec<_>>() != model.iter().collect::<Vec<_>>() {
        chips.set_chips(model);
    }
    chips.set_content_width(width);
    chips.set_content_height(30.0);

    let interval = flyout.get_interval();
    let docked = flyout.get_docked();
    let pinned = flyout.get_pinned();
    let mut bx = 697;
    let mut buttons = Vec::new();
    for (label, act, active) in [
        ("Refresh".to_string(), "refresh", false),
        (format!("Every {interval}s"), "interval-menu", false),
        ("Dock".to_string(), "flyout-dock", docked),
        ("Pin".to_string(), "pin", pinned),
    ] {
        let bw = text_width_font(&label, "Segoe UI", 10, true) + 16;
        bx -= bw;
        buttons.push(HeaderButtonData {
            px: bx as f32,
            py: 16.0,
            pw: bw as f32,
            ph: 22.0,
            label: label.into(),
            action: act.into(),
            active,
        });
        bx -= 6;
    }
    // Only replace the buttons model when it changed: the flyout re-renders every second while shown.
    if flyout.get_buttons().iter().ne(buttons.iter().cloned()) {
        flyout.set_buttons(Rc::new(VecModel::from(buttons)).into());
    }

    let mut raw_cards: Vec<(usize, &crate::core::contracts::ProviderSnapshot, Vec<LineData>, f32, f32, bool, bool)> = Vec::new();
    let mut heights = Vec::new();
    if let Some(s) = snapshot {
        for (index, p) in s.providers.iter().enumerate() {
            let is_ko = p.status.state == state::LOGGED_OUT;
            let is_unmet = p.status.state == state::NOT_INSTALLED;
            let mut lines = Vec::new();
            let mut y = 30.0;
            let mut label_width = 24;
            if p.status.state == state::STALE {
                let st = format::stale_text(p, now, &Local);
                let tag = if st.to_ascii_lowercase().contains("rate limited") {
                    "SLOW"
                } else {
                    "STALE"
                };
                lines.push(LineData {
                    kind: 4,
                    py: y,
                    ph: 14.0,
                    stat_tag: tag.into(),
                    text: capitalize_first(&st).into(),
                    ..Default::default()
                });
                y += 20.0;
            }
            let mut win_idx = 0;
            for w in &p.windows {
                if w.kind == kind::CREDITS && w.used_pct.is_none() {
                    lines.push(LineData {
                        kind: 1,
                        py: y,
                        ph: 14.0,
                        text: format!("ITEM: {}", w.label).into(),
                        label_color: color(0xffd700),
                        ..Default::default()
                    });
                    y += 20.0;
                    continue;
                }
                let label = format::flyout_label(w);
                label_width = label_width.max(text_width(&label, 11, true) + 2);
                let used = w.used_pct.unwrap_or(0.0).clamp(0.0, 100.0);
                let rem = w.remaining_pct.unwrap_or(100.0 - used).clamp(0.0, 100.0);
                let rem_int = rem.round() as i64;
                let rem_text = format!("{rem_int:>3}/100");
                let alert = crate::core::models::is_alerting(w);
                let (stat_tag, tag_col) = match win_idx {
                    0 => ("HP", 0xffd700),
                    1 => ("MP", 0x8cc8ff),
                    _ => ("SP", 0xd2b4ff),
                };
                win_idx += 1;
                let (pct_col, bar_top, bar_bottom, track_border) = if used >= 80.0 || alert {
                    (0xff5a5a, 0xf87171, 0xb91c1c, 0x8c2430)
                } else if used >= 50.0 {
                    (0xffd228, 0xfde047, 0xca8a04, 0x28417d)
                } else {
                    (0x50eb78, 0x4ade80, 0x16a34a, 0x28417d)
                };
                lines.push(LineData {
                    kind: 0,
                    py: y,
                    ph: 16.0,
                    label: label.into(),
                    pct: rem_text.into(),
                    eta: format::format_eta(w.resets_at, now, &Local).into(),
                    fill: used as f32,
                    alert,
                    stat_tag: stat_tag.into(),
                    tag_color: color(tag_col),
                    pct_color: color(pct_col),
                    label_color: color(if alert { 0xfecaca } else { 0xe6f0ff }),
                    bar_top: color(bar_top),
                    bar_bottom: color(bar_bottom),
                    track_border: color(track_border),
                    ..Default::default()
                });
                y += 22.0;
            }
            for note in p.footnotes.iter().filter(|n| !n.is_empty()) {
                lines.push(LineData {
                    kind: 3,
                    py: y,
                    ph: 14.0,
                    text: note.clone().into(),
                    label_color: color(0x8296b9),
                    ..Default::default()
                });
                y += 20.0;
            }
            if lines.is_empty() {
                if let Some(hint) = format::status_text(p) {
                    lines.push(LineData {
                        kind: 2,
                        py: y,
                        ph: 14.0,
                        text: hint.into(),
                        label_color: color(if is_ko { 0xd2aab4 } else { 0xb9cdeb }),
                        ..Default::default()
                    });
                    y += 20.0;
                }
            }
            let y_after = if lines.is_empty() { 30.0 } else { y - 6.0 };
            let h = y_after + 9.0;
            heights.push(h);
            raw_cards.push((index, p, lines, label_width.clamp(24, 104) as f32, y_after, is_ko, is_unmet));
        }
    }

    let eta_column = raw_cards
        .iter()
        .flat_map(|(_, _, lines, _, _, _, _)| lines.iter())
        .filter(|l| l.kind == 0 && !l.eta.is_empty())
        .map(|l| text_width(l.eta.as_str(), 11, false) + 2)
        .max()
        .unwrap_or(46)
        .max(46) as f32;

    let mut row_heights = Vec::new();
    for row in (0..heights.len()).step_by(2) {
        let h = heights[row].max(*heights.get(row + 1).unwrap_or(&40.0)).max(40.0);
        row_heights.push(h);
    }

    let mut cards = Vec::new();
    let mut y_card = 49.0;
    for (index, p, mut lines, label_w, y_after, is_ko, is_unmet) in raw_cards {
        let row = index / 2;
        let card_h = row_heights[row];
        if index % 2 == 0 && index > 0 {
            y_card += row_heights[row - 1] + 8.0;
        }
        let eta_left = 314.0 - eta_column;
        for line in &mut lines {
            if line.kind == 0 {
                let pct_w = (text_width_font(line.pct.as_str(), "Consolas", 11, true) + 2).max(46) as f32;
                let pct_left = eta_left - 6.0 - pct_w;
                let meter_x = label_w + 24.0;
                let meter_w = (pct_left - 6.0 - meter_x).max(24.0);
                let rem = (100.0 - line.fill).clamp(0.0, 100.0);
                let mut fill_w = ((meter_w - 2.0) * (rem / 100.0)).round().clamp(0.0, meter_w - 2.0);
                if rem > 0.0 && fill_w < 3.0 {
                    fill_w = 3.0;
                }
                line.meter_x = meter_x;
                line.meter_w = meter_w;
                line.fill_w = fill_w;
                line.pct_x = pct_left;
                line.pct_width = pct_w;
                line.eta_x = eta_left;
            }
        }
        if (is_ko || is_unmet) && lines.len() <= 1 && (card_h - y_after) >= 24.0 {
            let ghost_y = y_after + 5.0;
            let ghost_label = if is_ko { "  0/100" } else { " --/---" };
            let ghost_pct_w = (text_width_font(ghost_label, "Consolas", 11, true) + 2).max(46) as f32;
            let ghost_pct_left = eta_left - 6.0 - ghost_pct_w;
            let ghost_track_x = 48.0;
            let ghost_track_w = (ghost_pct_left - 6.0 - ghost_track_x).max(24.0);
            let ghost_col = color(if is_ko { 0xff5a5a } else { 0x8296b9 });
            lines.push(LineData {
                kind: 5,
                py: ghost_y,
                ph: 16.0,
                label: "--".into(),
                label_color: color(0x8296b9),
                stat_tag: "HP".into(),
                tag_color: ghost_col,
                meter_x: ghost_track_x,
                meter_w: ghost_track_w,
                fill_w: 0.0,
                track_border: color(if is_ko { 0x8c2430 } else { 0x28417d }),
                pct: ghost_label.into(),
                pct_x: ghost_pct_left,
                pct_width: ghost_pct_w,
                pct_color: ghost_col,
                eta: (if is_ko { "REVIVE" } else { "LOCKED" }).into(),
                eta_x: eta_left,
                ..Default::default()
            });
            let banner_y = ghost_y + 20.0;
            if banner_y + 15.0 <= card_h - 9.0 {
                lines.push(LineData {
                    kind: 6,
                    py: banner_y,
                    ph: 14.0,
                    text: (if is_ko {
                        "STATUS: KNOCKED OUT \u{00B7} RUN COMMAND TO REVIVE"
                    } else {
                        "STATUS: UNLOCKED ONCE INSTALLED"
                    })
                    .into(),
                    label_color: color(if is_ko { 0xeb7878 } else { 0x8caad7 }),
                    ..Default::default()
                });
            }
        }
        let (initial, accent) = brand_classic(&p.id);
        let (bg_top, bg_bottom) = if is_ko {
            (0x1c1826, 0x0a0812)
        } else if is_unmet {
            (0x161c30, 0x080a16)
        } else {
            (0x143088, 0x060e34)
        };
        let name_col = if is_ko {
            0xa59baf
        } else if is_unmet {
            0xb4c3e1
        } else {
            0xffffff
        };
        let (plan_text, plan_is_badge, plan_w, plan_bg, plan_border, plan_col) = if is_ko {
            ("KO".to_string(), true, 36.0, 0xc01818, 0xffd700, 0xffffff)
        } else if is_unmet {
            ("UNMET".to_string(), true, 48.0, 0x202846, 0x8ca5d2, 0xc8d7f5)
        } else if let Some(pl) = p.plan.as_deref().filter(|s| !s.is_empty()) {
            (format!("LV. {pl}"), false, 0.0, 0, 0, 0xffd700)
        } else {
            (String::new(), false, 0.0, 0, 0, 0xffd700)
        };
        cards.push(CardData {
            id: p.id.clone().into(),
            name: p.display_name.as_ref().unwrap_or(&p.id).clone().into(),
            plan: plan_text.into(),
            initial: initial.into(),
            accent: color(accent),
            lines: Rc::new(VecModel::from(lines)).into(),
            px: if index % 2 == 0 { 13.0 } else { 359.0 },
            py: y_card,
            pw: 338.0,
            ph: card_h,
            label_width: label_w,
            eta_width: eta_column,
            bg_top: color(bg_top),
            bg_bottom: color(bg_bottom),
            badge_border: color(if is_ko { 0x8296b9 } else { 0xffd700 }),
            name_color: color(name_col),
            plan_color: color(plan_col),
            plan_bg_top: color(plan_bg),
            plan_border: color(plan_border),
            plan_w,
            plan_is_badge,
            show_cursor: index == 0,
            cursor_indent: if index % 2 == 0 { 19.0 } else { 0.0 },
            ..Default::default()
        });
    }
    let total_h = if row_heights.is_empty() {
        120.0
    } else {
        let body_h: f32 = row_heights.iter().sum::<f32>() + 8.0 * ((row_heights.len() - 1) as f32);
        (12.0 + 28.0 + 8.0 + body_h + 12.0).max(120.0)
    };
    flyout.set_content_width(710.0);
    flyout.set_content_height(total_h);
    let key = format!(
        "rpg|{interval}|{docked}|{pinned}|{}",
        cards
            .iter()
            .map(|c| format!(
                "{:?}",
                (&c.id, &c.name, &c.plan, c.px, c.py, c.ph, c.lines.iter().collect::<Vec<_>>())
            ))
            .collect::<Vec<_>>()
            .join("|")
    );
    if flyout.get_render_key().as_str() != key {
        flyout.set_render_key(key.into());
        flyout.set_cards(Rc::new(VecModel::from(cards)).into());
    }
}

fn update_fluent(chips: &Chips, flyout: &Flyout, snapshot: Option<&Snapshot>, now: DateTime<Utc>) {
    let mut x = 7;
    let mut chip_data = Vec::new();
    if let Some(s) = snapshot {
        for p in &s.providers {
            let c = chip_model::for_provider(Some(p));
            let (initial, accent, badge_ink) = brand_fluent(&p.id);
            let w = 36 + text_width_font(&c.text, "Segoe UI Semibold", 11, false);
            let (glow, outline, ink) = if c.stale {
                let pen_col = match c.color_class {
                    "red" => color(0xef4444),
                    "green" => rgba(245, 158, 11, 220),
                    _ => color(0x6c727c),
                };
                (rgba(245, 158, 11, 24), pen_col, color(0xfcd34d))
            } else if c.color_class == "red" {
                (rgba(239, 68, 68, 35), color(0xef4444), color(0xfecaca))
            } else if c.color_class == "green" {
                (rgba(0, 0, 0, 0), rgba(255, 255, 255, 45), color(0xf5f6f8))
            } else {
                (rgba(0, 0, 0, 0), rgba(255, 255, 255, 42), color(0x94a3b8))
            };
            chip_data.push(ChipData {
                id: p.id.clone().into(),
                text: c.text.into(),
                initial: initial.into(),
                accent: color(accent),
                accent_light: lighten(accent, 0.16),
                accent_dark: darken(accent, 0.12),
                badge_ink: color(badge_ink),
                ink,
                outline,
                glow,
                stale: c.stale,
                px: x as f32,
                pw: w as f32,
                dash_path: if c.stale {
                    dashed_round_outline(w as f32, 22.0, 6.0).into()
                } else {
                    Default::default()
                },
                ..Default::default()
            });
            x += w + 6;
        }
    }
    if chip_data.is_empty() {
        let w = 36 + text_width_font("loading\u{2026}", "Segoe UI Semibold", 11, false);
        chip_data.push(ChipData {
            text: "loading\u{2026}".into(),
            initial: "\u{2026}".into(),
            accent: color(0x9aa0aa),
            accent_light: lighten(0x9aa0aa, 0.16),
            accent_dark: darken(0x9aa0aa, 0.12),
            badge_ink: color(0xffffff),
            ink: color(0x94a3b8),
            outline: rgba(255, 255, 255, 42),
            glow: rgba(0, 0, 0, 0),
            px: 7.0,
            pw: w as f32,
            ..Default::default()
        });
        x = 7 + w + 6;
    }
    let width = (x + 1).clamp(96, 720) as f32;
    let model: ModelRc<ChipData> = Rc::new(VecModel::from(chip_data)).into();
    if chips.get_chips().iter().collect::<Vec<_>>() != model.iter().collect::<Vec<_>>() {
        chips.set_chips(model);
    }
    chips.set_content_width(width);
    chips.set_content_height(30.0);

    let interval = flyout.get_interval();
    let docked = flyout.get_docked();
    let pinned = flyout.get_pinned();
    let mut bx = 685;
    let mut buttons = Vec::new();
    for (label, act, active) in [
        ("Refresh".to_string(), "refresh", false),
        (format!("Every {interval}s"), "interval-menu", false),
        ("Dock".to_string(), "flyout-dock", docked),
        ("Pin".to_string(), "pin", pinned),
    ] {
        let bw = text_width_font(&label, "Segoe UI Semibold", 11, false) + 20;
        bx -= bw;
        buttons.push(HeaderButtonData {
            px: bx as f32,
            py: 18.0,
            pw: bw as f32,
            ph: 24.0,
            label: label.into(),
            action: act.into(),
            active,
        });
        bx -= 6;
    }
    // Only replace the buttons model when it changed: the flyout re-renders every second while shown.
    if flyout.get_buttons().iter().ne(buttons.iter().cloned()) {
        flyout.set_buttons(Rc::new(VecModel::from(buttons)).into());
    }

    let mut raw_cards: Vec<(usize, &crate::core::contracts::ProviderSnapshot, Vec<LineData>, i32)> = Vec::new();
    let mut heights = Vec::new();
    if let Some(s) = snapshot {
        for (index, p) in s.providers.iter().enumerate() {
            let (_, accent, _) = brand_fluent(&p.id);
            let mut lines = Vec::new();
            let mut card_label_w = 28;
            if p.status.state == state::STALE {
                lines.push(LineData {
                    kind: 4,
                    ph: 22.0,
                    text: format::stale_text(p, now, &Local).into(),
                    ..Default::default()
                });
            }
            for w in &p.windows {
                if w.kind == kind::CREDITS && w.used_pct.is_none() {
                    let tag_w = (text_width(&format!("\u{25C6} {}", w.label), 11, false) + 14).min(302);
                    lines.push(LineData {
                        kind: 1,
                        ph: 20.0,
                        text: w.label.clone().into(),
                        tag_w: tag_w as f32,
                        ..Default::default()
                    });
                    continue;
                }
                let label = format::flyout_label(w);
                card_label_w = card_label_w.max(text_width_font(&label, "Segoe UI Semibold", 11, false) + 2);
                let used = w.used_pct.unwrap_or(0.0).clamp(0.0, 100.0) as f32;
                let alert = crate::core::models::is_alerting(w);
                let (bar_top, bar_bottom) = if used >= 100.0 {
                    (color(0xfc7878), color(0xc81c1c))
                } else if used >= 80.0 || alert {
                    (color(0xfde047), color(0xd97706))
                } else {
                    (lighten(accent, 0.30), darken(accent, 0.12))
                };
                let label_col = if used >= 100.0 {
                    color(0xfecaca)
                } else if used >= 80.0 || alert {
                    color(0xfef08a)
                } else {
                    color(0xdce0e6)
                };
                let pct_col = if used >= 100.0 {
                    color(0xf87171)
                } else if used >= 80.0 || alert {
                    color(0xfbbf24)
                } else {
                    color(0xf5f6f8)
                };
                lines.push(LineData {
                    kind: 0,
                    ph: 22.0,
                    label: label.into(),
                    pct: format::format_used_total(Some(w), false).into(),
                    eta: format::format_eta(w.resets_at, now, &Local).into(),
                    fill: used,
                    alert,
                    bar_top,
                    bar_bottom,
                    label_color: label_col,
                    pct_color: pct_col,
                    ..Default::default()
                });
            }
            for note in p.footnotes.iter().filter(|n| !n.is_empty()) {
                lines.push(LineData {
                    kind: 3,
                    ph: 18.0,
                    text: note.clone().into(),
                    ..Default::default()
                });
            }
            if lines.is_empty() {
                if let Some(hint) = format::status_text(p) {
                    lines.push(LineData {
                        kind: 2,
                        ph: 24.0,
                        text: hint.into(),
                        ..Default::default()
                    });
                }
            }
            let lines_h: f32 = if lines.is_empty() {
                0.0
            } else {
                lines.iter().map(|l| l.ph).sum::<f32>() + 6.0 * ((lines.len() - 1) as f32)
            };
            let card_h = 12.0 + 22.0 + 8.0 + lines_h + 12.0;
            heights.push(card_h);
            raw_cards.push((index, p, lines, card_label_w));
        }
    }

    let eta_column = raw_cards
        .iter()
        .flat_map(|(_, _, lines, _)| lines.iter())
        .filter(|l| l.kind == 0 && !l.eta.is_empty())
        .map(|l| text_width(l.eta.as_str(), 11, false) + 6)
        .max()
        .unwrap_or(46)
        .max(46) as f32;
    let pct_column = raw_cards
        .iter()
        .flat_map(|(_, _, lines, _)| lines.iter())
        .filter(|l| l.kind == 0 && !l.pct.is_empty())
        .map(|l| text_width_font(l.pct.as_str(), "Segoe UI Semibold", 11, false) + 2)
        .max()
        .unwrap_or(46)
        .max(46) as f32;

    let mut row_heights = Vec::new();
    for row in (0..heights.len()).step_by(2) {
        let h = heights[row].max(*heights.get(row + 1).unwrap_or(&40.0)).max(40.0);
        row_heights.push(h);
    }

    let mut cards = Vec::new();
    let mut y_card = 57.0;
    for (index, p, mut lines, card_label_w) in raw_cards {
        let row = index / 2;
        let card_h = row_heights[row];
        if index % 2 == 0 && index > 0 {
            y_card += row_heights[row - 1] + 10.0;
        }
        let max_lw = (302.0 - pct_column - eta_column - 40.0) as i32;
        let label_w = card_label_w.min(104).min(max_lw).max(24) as f32;
        let req_h: f32 = if lines.is_empty() {
            0.0
        } else {
            lines.iter().map(|l| l.ph).sum::<f32>() + 6.0 * ((lines.len() - 1) as f32)
        };
        let lines_area_h = card_h - (12.0 + 22.0 + 8.0 + 12.0);
        let extra_space = lines_area_h - req_h;
        let mut ly = 42.0;
        let mut extra_gap = 0.0;
        if extra_space > 4.0 {
            if lines.len() == 1 {
                ly += (extra_space as i32 / 2) as f32;
            } else if lines.len() > 1 {
                let per_gap = (extra_space as i32 / (lines.len() as i32 - 1)).min(6);
                extra_gap = per_gap as f32;
                let distributed = per_gap * (lines.len() as i32 - 1);
                ly += ((extra_space as i32 - distributed) / 2) as f32;
            }
        }
        let eta_left = 302.0 - eta_column;
        let pct_left = eta_left - 6.0 - pct_column;
        let meter_x = label_w + 6.0;
        let meter_w = (pct_left - 6.0 - meter_x).max(20.0);
        for (i, line) in lines.iter_mut().enumerate() {
            if i > 0 {
                ly += 6.0 + extra_gap;
            }
            line.py = ly;
            if line.kind == 0 {
                line.meter_x = meter_x;
                line.meter_w = meter_w;
                let mut fw = (meter_w * (line.fill / 100.0)).round().clamp(0.0, meter_w);
                if line.fill > 0.0 && fw < 10.0_f32.min(meter_w) {
                    fw = 10.0_f32.min(meter_w);
                }
                line.fill_w = fw;
                line.pct_x = pct_left;
                line.pct_width = pct_column;
                line.eta_x = eta_left;
            }
            ly += line.ph;
        }
        let (initial, accent, badge_ink) = brand_fluent(&p.id);
        let plan_str = p.plan.clone().unwrap_or_default();
        let plan_w = if plan_str.is_empty() {
            0.0
        } else {
            (text_width(&plan_str, 11, false) + 12) as f32
        };
        let r = ((accent >> 16) & 0xff) as u8;
        let g = ((accent >> 8) & 0xff) as u8;
        let b = (accent & 0xff) as u8;
        cards.push(CardData {
            id: p.id.clone().into(),
            name: p.display_name.as_ref().unwrap_or(&p.id).clone().into(),
            plan: plan_str.into(),
            initial: initial.into(),
            accent: color(accent),
            accent_light: lighten(accent, 0.16),
            accent_dark: darken(accent, 0.12),
            badge_ink: color(badge_ink),
            glow_top: rgba(r, g, b, 56),
            glow_mid: rgba(r, g, b, 18),
            glow_end: rgba(r, g, b, 0),
            plan_w,
            lines: Rc::new(VecModel::from(lines)).into(),
            px: if index % 2 == 0 { 15.0 } else { 355.0 },
            py: y_card,
            pw: 330.0,
            ph: card_h,
            label_width: label_w,
            eta_width: eta_column,
            ..Default::default()
        });
    }
    let total_h = if row_heights.is_empty() {
        120.0
    } else {
        let body_h: f32 = row_heights.iter().sum::<f32>() + 10.0 * ((row_heights.len() - 1) as f32);
        (14.0 + 32.0 + 10.0 + body_h + 14.0).max(120.0)
    };
    flyout.set_content_width(700.0);
    flyout.set_content_height(total_h);
    let key = format!(
        "fluent|{interval}|{docked}|{pinned}|{}",
        cards
            .iter()
            .map(|c| format!(
                "{:?}",
                (&c.id, &c.name, &c.plan, c.px, c.py, c.ph, c.lines.iter().collect::<Vec<_>>())
            ))
            .collect::<Vec<_>>()
            .join("|")
    );
    if flyout.get_render_key().as_str() != key {
        flyout.set_render_key(key.into());
        flyout.set_cards(Rc::new(VecModel::from(cards)).into());
    }
}

fn update_terminal(chips: &Chips, flyout: &Flyout, snapshot: Option<&Snapshot>, now: DateTime<Utc>) {
    let mut x = 7;
    let mut chip_data = Vec::new();
    if let Some(s) = snapshot {
        for p in &s.providers {
            let c = chip_model::for_provider(Some(p));
            let (initial, accent) = brand_terminal(&p.id);
            let w = 32 + text_width_font(&c.text, "Consolas", 11, true);
            let bg_top = if c.color_class == "red" {
                0x280e0e
            } else if c.stale {
                0x221a0c
            } else if c.color_class == "gray" {
                0x101612
            } else {
                0x0c120e
            };
            let outline = if c.color_class == "red" {
                0xff4444
            } else if c.color_class == "green" {
                if c.stale { 0xffb000 } else { 0x33ff55 }
            } else {
                0x4b825f
            };
            let ink = if c.stale {
                0xffb000
            } else if c.color_class == "red" {
                0xff5555
            } else if c.color_class == "green" {
                0x33ff55
            } else {
                0x72b085
            };
            chip_data.push(ChipData {
                id: p.id.clone().into(),
                text: c.text.into(),
                initial: initial.into(),
                accent: color(accent),
                bg_top: color(bg_top),
                ink: color(ink),
                outline: color(outline),
                stale: c.stale,
                px: x as f32,
                pw: w as f32,
                dash_path: if c.stale {
                    dashed_rect_outline(w as f32, 22.0).into()
                } else {
                    Default::default()
                },
                ..Default::default()
            });
            x += w + 6;
        }
    }
    if chip_data.is_empty() {
        let w = 32 + text_width_font("loading\u{2026}", "Consolas", 11, true);
        chip_data.push(ChipData {
            text: "loading\u{2026}".into(),
            initial: ">".into(),
            accent: color(0x6eb482),
            bg_top: color(0x101612),
            ink: color(0x72b085),
            outline: color(0x4b825f),
            px: 7.0,
            pw: w as f32,
            ..Default::default()
        });
        x = 7 + w + 6;
    }
    let width = (x + 1).clamp(96, 720) as f32;
    let model: ModelRc<ChipData> = Rc::new(VecModel::from(chip_data)).into();
    if chips.get_chips().iter().collect::<Vec<_>>() != model.iter().collect::<Vec<_>>() {
        chips.set_chips(model);
    }
    chips.set_content_width(width);
    chips.set_content_height(30.0);

    let interval = flyout.get_interval();
    let docked = flyout.get_docked();
    let pinned = flyout.get_pinned();
    let mut bx = 687;
    let mut buttons = Vec::new();
    for (label, act, active) in [
        ("[REFRESH]".to_string(), "refresh", false),
        (format!("[{interval}s]"), "interval-menu", false),
        (
            if docked { "[DOCK:ON]" } else { "[DOCK]" }.to_string(),
            "flyout-dock",
            docked,
        ),
        (
            if pinned { "[PIN:ON]" } else { "[PIN]" }.to_string(),
            "pin",
            pinned,
        ),
    ] {
        let bw = text_width_font(&label, "Consolas", 11, true) + 12;
        bx -= bw;
        buttons.push(HeaderButtonData {
            px: bx as f32,
            py: 16.0,
            pw: bw as f32,
            ph: 22.0,
            label: label.into(),
            action: act.into(),
            active,
        });
        bx -= 5;
    }
    // Only replace the buttons model when it changed: the flyout re-renders every second while shown.
    if flyout.get_buttons().iter().ne(buttons.iter().cloned()) {
        flyout.set_buttons(Rc::new(VecModel::from(buttons)).into());
    }

    let mut any_alert = false;
    let mut any_stale = false;
    let mut raw_cards: Vec<(usize, &crate::core::contracts::ProviderSnapshot, Vec<LineData>, i32, i32, bool, bool, bool)> = Vec::new();
    let mut heights = Vec::new();
    if let Some(s) = snapshot {
        for (index, p) in s.providers.iter().enumerate() {
            let is_stale = p.status.state == state::STALE;
            if is_stale {
                any_stale = true;
            }
            let mut lines = Vec::new();
            let mut y = 30.0;
            let mut card_label_w = 24;
            let mut card_pct_w = 24;
            if is_stale {
                lines.push(LineData {
                    kind: 4,
                    py: y,
                    ph: 14.0,
                    text: format!("[!] {}", format::stale_text(p, now, &Local)).into(),
                    label_color: color(0xffb000),
                    ..Default::default()
                });
                y += 20.0;
            }
            for w in &p.windows {
                if w.kind == kind::CREDITS && w.used_pct.is_none() {
                    lines.push(LineData {
                        kind: 1,
                        py: y,
                        ph: 14.0,
                        text: format!("> {}", w.label).into(),
                        label_color: color(0x50c8ff),
                        ..Default::default()
                    });
                    y += 20.0;
                    continue;
                }
                let label = format::flyout_label(w);
                card_label_w = card_label_w.max(text_width_font(&label, "Consolas", 11, true) - 3);
                let pct = format::format_used_total(Some(w), false);
                card_pct_w = card_pct_w.max(text_width_font(&pct, "Consolas", 11, true));
                let used = w.used_pct.unwrap_or(0.0).clamp(0.0, 100.0) as f32;
                let alert = crate::core::models::is_alerting(w);
                if alert || used >= 100.0 {
                    any_alert = true;
                }
                lines.push(LineData {
                    kind: 0,
                    py: y,
                    ph: 16.0,
                    label: label.into(),
                    pct: pct.into(),
                    eta: format::format_eta(w.resets_at, now, &Local).into(),
                    fill: used,
                    alert,
                    ..Default::default()
                });
                y += 22.0;
            }
            for note in p.footnotes.iter().filter(|n| !n.is_empty()) {
                lines.push(LineData {
                    kind: 3,
                    py: y,
                    ph: 14.0,
                    text: format!("# {note}").into(),
                    label_color: color(0x4b825f),
                    ..Default::default()
                });
                y += 20.0;
            }
            let hint_opt = format::status_text(p);
            if lines.is_empty() {
                if let Some(hint) = &hint_opt {
                    lines.push(LineData {
                        kind: 2,
                        py: y,
                        ph: 14.0,
                        text: format!("$ {hint}").into(),
                        label_color: color(0xffb000),
                        ..Default::default()
                    });
                    y += 20.0;
                }
            }
            let is_offline = hint_opt.is_some() && (lines.is_empty() || (lines.len() == 1 && lines[0].kind == 2));
            let is_card_alert = lines.iter().any(|l| l.alert || l.fill >= 100.0);
            let h = if lines.is_empty() { 39.0 } else { y - 6.0 + 9.0 };
            heights.push(h);
            raw_cards.push((
                index,
                p,
                lines,
                card_label_w.min(104),
                card_pct_w.min(46),
                is_stale,
                is_offline,
                is_card_alert,
            ));
        }
    }

    let tag_left = format!("[ {} MONITORS ]", raw_cards.len());
    let tag_left_w = text_width_font(&tag_left, "Consolas", 10, false) as f32;
    let (tag_center, tag_center_col) = if any_alert {
        ("[ SYS HEALTH: ALERT ]", 0xff4444)
    } else if any_stale {
        ("[ SYS HEALTH: STALE ]", 0xffb000)
    } else {
        ("[ SYS HEALTH: OK ]", 0x33ff55)
    };
    let tag_center_w = text_width_font(tag_center, "Consolas", 10, false) as f32;
    flyout.set_telemetry_left(tag_left.into());
    flyout.set_telemetry_left_w(tag_left_w);
    flyout.set_telemetry_center(tag_center.into());
    flyout.set_telemetry_center_w(tag_center_w);
    flyout.set_telemetry_color(color(tag_center_col));

    let eta_column = raw_cards
        .iter()
        .flat_map(|(_, _, lines, _, _, _, _, _)| lines.iter())
        .filter(|l| l.kind == 0 && !l.eta.is_empty())
        .map(|l| text_width_font(l.eta.as_str(), "Consolas", 11, false) + 4)
        .max()
        .unwrap_or(46)
        .max(46) as f32;

    let mut row_heights = Vec::new();
    for row in (0..heights.len()).step_by(2) {
        let h = heights[row].max(*heights.get(row + 1).unwrap_or(&40.0)).max(40.0);
        row_heights.push(h);
    }

    let char_w = 6.7_f32;
    let bracket_w = char_w;

    let mut cards = Vec::new();
    let mut y_card = 57.0;
    for (index, p, mut lines, label_w, pct_w, is_stale, is_offline, is_card_alert) in raw_cards {
        let row = index / 2;
        let card_h = row_heights[row];
        if index % 2 == 0 && index > 0 {
            y_card += row_heights[row - 1] + 8.0;
        }
        let lw = label_w as f32;
        let pw = pct_w as f32;
        let eta_left = 317.0 - eta_column;
        let pct_left = eta_left - 6.0 - pw;
        let meter_x = lw + 6.0;
        let meter_w = (pct_left - 6.0 - meter_x).max(30.0);
        let total_slots = (((meter_w - bracket_w * 2.0) / char_w) as i32).max(6);
        for line in &mut lines {
            if line.kind == 0 {
                let mut filled = ((total_slots as f32 * (line.fill / 100.0)).round() as i32).clamp(0, total_slots);
                if filled >= total_slots && line.fill < 100.0 {
                    filled = total_slots - 1;
                }
                let empty = total_slots - filled;
                line.ascii_filled = "#".repeat(filled as usize).into();
                line.ascii_empty = ".".repeat(empty as usize).into();
                let (bar_col, dot_col, bracket_col) = if line.alert || line.fill >= 100.0 {
                    (0xff4444, 0x782a2a, 0xff4444)
                } else if line.fill >= 80.0 {
                    (0xffb000, 0x785519, 0xffb000)
                } else {
                    (0x33ff55, 0x3a7d4b, 0x5ac373)
                };
                line.label_color = color(if line.alert { 0xff4444 } else { 0x8ce69b });
                line.bar_top = color(bar_col);
                line.pct_color = color(bar_col);
                line.dot_color = color(dot_col);
                line.bracket_color = color(bracket_col);
                line.meter_x = meter_x;
                line.meter_w = meter_w;
                line.pct_x = pct_left;
                line.pct_width = pw;
                line.eta_x = eta_left;
            }
        }
        let (initial, accent) = brand_terminal(&p.id);
        let (head_bg, border_col, cross_col) = if is_card_alert {
            (0x280e0e, 0x6e2828, 0xff4444)
        } else if is_stale {
            (0x221a0c, 0x644b19, 0xffb000)
        } else if is_offline {
            (0x101612, 0x284630, 0x4b825f)
        } else {
            (0x121e16, 0x265f30, 0x33ff55)
        };
        let has_hint = format::status_text(p).is_some();
        let (status_tag, status_col) = if is_stale {
            ("[STALE]".to_string(), 0xffb000)
        } else if has_hint {
            ("[OFFLINE]".to_string(), 0xffb000)
        } else if let Some(pl) = p.plan.as_deref().filter(|s| !s.is_empty()) {
            (format!("[{pl}]"), 0x82c896)
        } else {
            (String::new(), 0x82c896)
        };
        cards.push(CardData {
            id: p.id.clone().into(),
            name: p.display_name.as_ref().unwrap_or(&p.id).clone().into(),
            plan: status_tag.into(),
            initial: initial.into(),
            accent: color(accent),
            head_bg: color(head_bg),
            border_color: color(border_col),
            cross_color: color(cross_col),
            name_color: color(if is_offline { 0xaac3b4 } else { 0xe1ffeb }),
            plan_color: color(status_col),
            lines: Rc::new(VecModel::from(lines)).into(),
            px: if index % 2 == 0 { 13.0 } else { 354.0 },
            py: y_card,
            pw: 333.0,
            ph: card_h,
            label_width: lw,
            eta_width: eta_column,
            ..Default::default()
        });
    }
    let total_h = if row_heights.is_empty() {
        120.0
    } else {
        let body_h: f32 = row_heights.iter().sum::<f32>() + 8.0 * ((row_heights.len() - 1) as f32);
        (12.0 + 28.0 + 16.0 + body_h + 12.0).max(120.0)
    };
    flyout.set_content_width(700.0);
    flyout.set_content_height(total_h);
    let key = format!(
        "terminal|{interval}|{docked}|{pinned}|{}",
        cards
            .iter()
            .map(|c| format!(
                "{:?}",
                (&c.id, &c.name, &c.plan, c.px, c.py, c.ph, c.lines.iter().collect::<Vec<_>>())
            ))
            .collect::<Vec<_>>()
            .join("|")
    );
    if flyout.get_render_key().as_str() != key {
        flyout.set_render_key(key.into());
        flyout.set_cards(Rc::new(VecModel::from(cards)).into());
    }
}

pub fn update(chips: &Chips, flyout: &Flyout, snapshot: Option<&Snapshot>, now: DateTime<Utc>) {
    let skin = skins::component_skin(Some(chips.get_skin().as_str()));
    if chips.get_skin().as_str() != skin {
        chips.set_skin(skin.into());
    }
    if flyout.get_skin().as_str() != skin {
        flyout.set_skin(skin.into());
    }
    match skin {
        "rpg" => update_rpg(chips, flyout, snapshot, now),
        "fluent" => update_fluent(chips, flyout, snapshot, now),
        "terminal" => update_terminal(chips, flyout, snapshot, now),
        _ => update_classic(chips, flyout, snapshot, now),
    }
}

struct Headless {
    windows: Rc<RefCell<Vec<Rc<MinimalSoftwareWindow>>>>,
}

impl Platform for Headless {
    fn create_window_adapter(&self) -> Result<Rc<dyn WindowAdapter>, slint::PlatformError> {
        let w = MinimalSoftwareWindow::new(RepaintBufferType::NewBuffer);
        self.windows.borrow_mut().push(w.clone());
        Ok(w)
    }
}

pub fn install_headless() -> Result<Rc<RefCell<Vec<Rc<MinimalSoftwareWindow>>>>, slint::PlatformError> {
    let windows = Rc::new(RefCell::new(Vec::new()));
    slint::platform::set_platform(Box::new(Headless {
        windows: windows.clone(),
    }))
    .map_err(|e| slint::PlatformError::from(e.to_string()))?;
    Ok(windows)
}

pub fn physical_size(w: f32, h: f32, scale: f32) -> slint::PhysicalSize {
    slint::PhysicalSize::new((w * scale).round() as u32, (h * scale).round() as u32)
}

pub fn pixels(window: &MinimalSoftwareWindow, w: u32, h: u32, scale: f32) -> Vec<u8> {
    window.dispatch_event(slint::platform::WindowEvent::ScaleFactorChanged {
        scale_factor: scale,
    });
    window.set_size(slint::PhysicalSize::new(w, h));
    let mut pixels = vec![Rgb8Pixel::default(); (w * h) as usize];
    // render() directly: no OS adapter, no show(), no event loop.
    window.request_redraw();
    window.draw_if_needed(|renderer| {
        renderer.render(&mut pixels, w as usize);
    });
    pixels.into_iter().flat_map(|p| [p.r, p.g, p.b]).collect()
}

pub fn load_snapshot(data_dir: &Path, exe_dir: &Path) -> Option<Snapshot> {
    FileSnapshotStore::new(data_dir.join("snapshot.json"))
        .load()
        .or_else(|| {
            exe_dir
                .ancestors()
                .find_map(|p| FileSnapshotStore::new(p.join("fixtures/snapshot-sample.json")).load())
        })
}

pub fn run(
    out: &Path,
    data: &Path,
    exe: &Path,
    skin: Option<&str>,
) -> Result<(), Box<dyn std::error::Error>> {
    if out.as_os_str().is_empty() {
        return Err(std::io::Error::new(
            std::io::ErrorKind::InvalidInput,
            "output directory is required",
        )
        .into());
    }
    std::fs::create_dir_all(out)?;
    let windows = install_headless()?;
    let chips = Chips::new()?;
    let flyout = Flyout::new()?;
    let skin_id = skins::component_skin(skin);
    chips.set_skin(skin_id.into());
    flyout.set_skin(skin_id.into());
    update(&chips, &flyout, load_snapshot(data, exe).as_ref(), Utc::now());
    #[cfg(windows)]
    let scale = crate::win::dpi::scale();
    #[cfg(not(windows))]
    let scale = 1.0;
    for (index, name, width, height) in [
        (0, "chips.png", chips.get_content_width(), chips.get_content_height()),
        (1, "flyout.png", flyout.get_content_width(), flyout.get_content_height()),
    ] {
        let slint::PhysicalSize { width: w, height: h } = physical_size(width, height, scale);
        let data = pixels(&windows.borrow()[index], w, h, scale);
        let mut encoder = png::Encoder::new(std::fs::File::create(out.join(name))?, w, h);
        encoder.set_color(png::ColorType::Rgb);
        encoder.set_depth(png::BitDepth::Eight);
        encoder.write_header()?.write_image_data(&data)?;
    }
    Ok(())
}
