//! Integer-only geometry port of TaskbarMath.cs; no platform dependencies.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct RectI { pub x: i32, pub y: i32, pub w: i32, pub h: i32 }
impl RectI {
    pub const fn new(x: i32, y: i32, w: i32, h: i32) -> Self { Self { x, y, w, h } }
}
pub fn edge(s: RectI, a: RectI) -> &'static str {
    let gaps = [a.y-s.y, a.x-s.x, s.x+s.w-a.x-a.w, s.y+s.h-a.y-a.h];
    let max = *gaps.iter().max().unwrap();
    if max <= 8 { return "bottom"; }
    ["top", "left", "right", "bottom"][gaps.iter().position(|g| *g == max).unwrap()]
}
pub fn dock_before_tray(t: RectI, n: RectI, w: i32, h: i32, gap: i32) -> RectI {
    if t.w >= t.h { RectI::new(n.x-gap-w, t.y+(t.h-h)/2, w, h) }
    else { RectI::new(t.x+(t.w-w)/2, n.y-gap-h, w, h) }
}
pub fn clamp(r: RectI, b: RectI) -> RectI {
    RectI::new(r.x.min(b.x+b.w-r.w).max(b.x), r.y.min(b.y+b.h-r.h).max(b.y), r.w, r.h)
}
pub fn is_over_tray(w: RectI, t: Option<RectI>) -> bool {
    t.is_some_and(|t| { let (x,y)=(w.x+w.w/2,w.y+w.h/2); x>=t.x && x<t.x+t.w && y>=t.y && y<t.y+t.h })
}
pub fn flyout_anchor(c: RectI, a: RectI, w: i32, h: i32, gap: i32) -> RectI {
    let mut y=c.y-gap-h;
    if y<a.y { y=c.y+c.h+gap; }
    clamp(RectI::new(c.x+(c.w-w)/2,y,w,h),a)
}
