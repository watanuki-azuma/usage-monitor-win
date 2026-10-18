use super::{chip_model as chip,taskbar_math::*};
use crate::core::{contracts::*,app_config::{AppConfig,SKIN_IDS},models};
use chrono::{Utc,Duration};
fn p(id:&str,state:&str,ws:&[(&str,f64)])->ProviderSnapshot{let mut p=ProviderSnapshot::new(id,Utc::now());p.status.state=state.into();p.windows=ws.iter().map(|(k,v)|models::window_of(k,k,&serde_json::json!(v),None)).collect();p}
#[test] fn test_edge_picks_the_thickest_gap_and_defaults_to_bottom(){
    let s=RectI::new(0,0,1920,1080);
    for (a,e) in [(RectI::new(0,0,1920,1040),"bottom"),(RectI::new(0,40,1920,1040),"top"),(RectI::new(60,0,1860,1080),"left"),(RectI::new(0,0,1860,1080),"right"),(s,"bottom"),(RectI::new(6,0,1914,1080),"bottom")] {assert_eq!(edge(s,a),e);}
    assert_eq!(edge(RectI::new(0,-1080,1920,1080),RectI::new(0,-1080,1920,1040)),"bottom");
}
#[test] fn test_dock_before_tray_horizontal_and_vertical(){
    assert_eq!(dock_before_tray(RectI::new(0,1040,1920,40),RectI::new(1500,1040,420,40),300,28,4),RectI::new(1196,1046,300,28));
    assert_eq!(dock_before_tray(RectI::new(0,0,60,1080),RectI::new(0,900,60,180),48,120,4),RectI::new(6,776,48,120));
    assert_eq!(dock_before_tray(RectI::new(0,-40,1920,40),RectI::new(1500,-40,420,40),300,28,4),RectI::new(1196,-34,300,28));
}
#[test] fn test_clamp_keeps_rectangles_on_screen(){
    let a=RectI::new(0,0,1920,1040);
    for (r,x,y) in [(RectI::new(100,100,300,28),100,100),(RectI::new(1800,100,300,28),1620,100),(RectI::new(-50,100,300,28),0,100),(RectI::new(100,1035,300,28),100,1012),(RectI::new(100,-20,300,28),100,0),(RectI::new(500,500,3000,28),0,500)] {assert_eq!(clamp(r,a),RectI::new(x,y,r.w,r.h));}
    assert_eq!(clamp(RectI::new(-100,10,300,28),RectI::new(-1920,0,1920,1040)),RectI::new(-300,10,300,28));
}
#[test] fn test_is_over_tray_uses_the_window_centre(){let t=Some(RectI::new(1500,1040,420,40));assert!(is_over_tray(RectI::new(1600,1042,100,28),t));assert!(!is_over_tray(RectI::new(1400,1042,100,28),t));assert!(!is_over_tray(RectI::new(1600,900,100,28),t));assert!(!is_over_tray(RectI::new(1600,1042,100,28),None));}
#[test] fn test_flyout_anchor_prefers_above_and_clamps_horizontally(){let a=RectI::new(0,0,1920,1040);for (c,r) in [(RectI::new(1200,1010,300,28),RectI::new(1060,722,580,280)),(RectI::new(1700,1010,300,28),RectI::new(1340,722,580,280)),(RectI::new(100,4,300,28),RectI::new(0,40,580,280))]{assert_eq!(flyout_anchor(c,a,580,280,8),r);}}
#[test] fn test_chip_text_colour_and_stale_flag(){
    let c=chip::for_provider(Some(&p("claude",state::OK,&[(kind::FIVE_HOUR,42.4)])));assert_eq!(c.text,"42/100");assert_eq!(c.color_class,"green");assert!(!c.stale);assert_eq!(c.id,"claude");
    assert_eq!(chip::for_provider(Some(&p("codex",state::OK,&[(kind::FIVE_HOUR,80.)]))).color_class,"red");
    let c=chip::for_provider(Some(&p("grok",state::LOGGED_OUT,&[])));assert_eq!(c.text,"—");assert_eq!(c.color_class,"gray");
    let c=chip::for_provider(Some(&p("gemini",state::STALE,&[(kind::DAILY,10.)])));assert!(c.stale);assert_eq!(c.color_class,"green");
    assert_eq!(chip::for_provider(Some(&p("claude",state::OK,&[(kind::WEEKLY,99.),(kind::FIVE_HOUR,5.)]))).text,"5/100");
    assert_eq!(chip::for_provider(None).text,"—");
}
#[test] fn test_key_changes_only_when_something_visible_changes(){
    let mut a=Snapshot::new(Utc::now());a.providers=vec![p("claude",state::OK,&[(kind::FIVE_HOUR,42.4)]),p("grok",state::LOGGED_OUT,&[])];let mut b=a.clone();b.generated_at+=Duration::minutes(5);b.providers[0].fetched_at+=Duration::minutes(5);assert_eq!(chip::key(Some(&a)),chip::key(Some(&b)));
    b.providers[0].windows[0].used_pct=Some(42.6);assert_ne!(chip::key(Some(&a)),chip::key(Some(&b)));b=a.clone();b.providers[1].status.state=state::STALE.into();assert_ne!(chip::key(Some(&a)),chip::key(Some(&b)));assert_eq!(chip::key(None),"");
}
#[test] fn test_config_skin_defaults_roundtrips_and_unknown_is_classic(){
    assert_eq!(AppConfig::default().skin,"classic");for json in ["{}",r#"{"skin":"bogus"}"#,r#"{"skin":5}"#]{assert_eq!(AppConfig::parse(Some(json)).skin,"classic");}
    for id in SKIN_IDS{let mut c=AppConfig::default();c.skin=id.into();let json=c.to_json();assert!(json.contains("\"skin\""));assert_eq!(AppConfig::parse(Some(&json)).skin,id);}
}
#[test] fn test_registry_creates_every_skin_and_unknown_is_classic(){assert_eq!(super::skins::normalize_skin(Some("nope")),"classic");assert_eq!(super::skins::normalize_skin(None),"classic");assert_eq!(SKIN_IDS.len(),4);assert_eq!(super::skins::name_of(Some("rpg")),"RPG");for id in SKIN_IDS{assert_eq!(super::skins::component_skin(Some(id)),"classic");}}
#[test] fn test_each_skin_measures_and_paints_chips_and_flyout(){
    // One isolated software platform on this test thread; no native windows or real data paths.
    let windows=super::render_test::install_headless().unwrap();
    let mut s=Snapshot::new(Utc::now());s.providers=vec![p("claude",state::OK,&[(kind::FIVE_HOUR,10.),(kind::WEEKLY,40.)]),p("codex",state::OK,&[(kind::FIVE_HOUR,85.),(kind::WEEKLY,40.)]),p("gemini",state::OK,&[]),p("grok",state::OK,&[(kind::FIVE_HOUR,100.),(kind::WEEKLY,40.)])];
    for (i,id) in SKIN_IDS.iter().enumerate(){assert_eq!(super::skins::component_skin(Some(id)),"classic");let c=super::Chips::new().unwrap();let f=super::Flyout::new().unwrap();super::render_test::update(&c,&f,Some(&s),Utc::now());for (index,width,height) in [(i*2,c.get_content_width(),30.),(i*2+1,640.,f.get_content_height())]{
        for scale in [1.,1.5,2.] {
            let slint::PhysicalSize {width:w,height:h}=super::render_test::physical_size(width,height,scale);
            let window=&windows.borrow()[index];
            let pixels=super::render_test::pixels(window,w,h,scale);
            assert_eq!(window.scale_factor(),scale);
            assert_eq!(pixels.len(),(w*h*3) as usize);assert!(pixels.chunks_exact(3).any(|p|p[0]>150));
            // A card/chip near the right edge must move with the design scale, not clip.
            let (x,y)=if index%2==0 {(width-8.,15.)}else{(620.,60.)};
            let offset=(((y*scale) as u32*w+(x*scale) as u32)*3) as usize;
            assert_ne!(&pixels[offset..offset+3],&[17,24,39]);
        }
    }}
}
#[test] fn test_render_size_scales_and_rounds_like_theme() {
    for (scale,w,h) in [(1.,640,263),(1.5,960,395),(2.,1280,526)] {
        assert_eq!(super::render_test::physical_size(640.,263.,scale),slint::PhysicalSize::new(w,h));
    }
}
