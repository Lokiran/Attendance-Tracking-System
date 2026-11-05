# Design system: colour (60-30-10)

The frontend colour palette follows the **60-30-10 rule**. Every colour token belongs to one of three roles.
Status and chart colours sit outside the split because they encode data.

All tokens live in [`frontend/src/index.css`](../frontend/src/index.css). Components use the Tailwind
utilities mapped from them (`bg-secondary`, `text-brand-text`, …) and never hard-coded hex values.

| Role | Share | What it is | Where it is used |
|---|---|---|---|
| **Dominant** | ~60 % | Neutral canvas | Page background, cards, inputs, body text |
| **Secondary** | ~30 % | Navy | Sidebar, top bar, page-title bands, the Today hero band, tinted table headers |
| **Accent** | up to 10 % | Bright blue | Primary buttons, active nav item, links, focus ring, progress, KPI icon chips, primary chart series |

The accent share is a **ceiling, not a fill target**. Accent marks what the user should act on or look at
first. Measured by area it is about 1–1.5 % of a page, and it carries most of the visual emphasis.
Spreading it further would make everything look clickable.

## Tokens

| Token | Light | Dark | Role |
|---|---|---|---|
| `--bg` | `#f8fafc` | `#0b1220` | Dominant: page canvas |
| `--surface` | `#ffffff` | `#151e2e` | Dominant: cards |
| `--surface-2` / `-3` | `#f1f5f9` / `#e2e8f0` | `#1b2638` / `#22304a` | Dominant: hover, wells |
| `--text` / `-2` / `-3` | `#0f172a` / `#334155` / `#64748b` | `#e2e8f0` / `#a9b6c9` / `#8492a8` | Dominant: text |
| `--secondary` | `#0f172a` | `#1d2a44` | Secondary: navy bands |
| `--secondary-raised` | `#1e293b` | `#26365a` | Secondary: hover/selected on navy |
| `--secondary-soft` | `#e8edf5` | `#1b2638` | Secondary tint: table headers |
| `--on-secondary` / `-2` / `-muted` | `#f1f5f9` / `#cbd5e1` / `#94a3b8` | `#e2e8f0` / `#cbd5e1` / `#94a3b8` | Text on navy |
| `--brand` | `#2563eb` | `#60a5fa` | Accent: buttons, active item |
| `--brand-text` | `#1d4ed8` | `#93c5fd` | Accent as text (links) |
| `--brand-soft` | `#eff6ff` | `#172554` | Accent tint: icon chips |
| `--accent-on-secondary` | `#60a5fa` | `#60a5fa` | Accent placed on navy |

## The `tone-secondary` scope

Add `className="tone-secondary"` to any element to make it a navy band. Inside it, the surface, text and
border tokens are remapped to their on-navy versions. Badges, ghost buttons, inputs and links stay
readable without per-component overrides. Primary (accent) buttons keep the accent colour.
`PageHeader` and the Today hero band in `AttendanceActionCard` use it.

## Rules for new UI

1. New surfaces use dominant tokens. Use navy only for structure (navigation, page titles, hero bands),
   never for content cards.
2. Use accent only for something interactive or a single focal point. One primary button per view.
3. Status colours (`--good`, `--warning`, `--critical`, `--st-*`) always come with an icon and a label,
   never colour alone.
4. Contrast: text needs at least 4.5:1 against its background, and indicators/borders that carry meaning
   need at least 3:1. Check new pairs in both themes.
5. Chart colours are validated against `--surface` in both themes with the dataviz palette validator.
   Re-run it when you change a series colour.

## Measured split (1440×900, seeded demo data)

Pixel share of each role, as dominant / secondary / accent / other (data, text, status):

| Page | Light | Dark |
|---|---|---|
| Employee dashboard | 64.2 / 31.7 / 1.4 / 2.7 | 63.9 / 31.6 / 1.4 / 3.1 |
| Team attendance (manager) | 66.1 / 29.4 / 1.0 / 3.5 | 66.2 / 29.1 / 1.0 / 3.7 |
| Organisation (admin) | 66.7 / 29.0 / 1.3 / 3.1 | 66.8 / 28.7 / 1.3 / 3.3 |
