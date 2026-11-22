"use strict";
(() => {
  const root=document.documentElement, prefers=matchMedia("(prefers-color-scheme: dark)");
  let saved;
  try {saved=localStorage.getItem("theoriacast.theme");} catch {}
  function update() {
    root.dataset.theme=["dark","light"].includes(saved)?saved:prefers.matches?"dark":"light";
    const b=document.querySelector(".theme");
    if(b){const dark=root.dataset.theme==="dark";b.hidden=false;b.setAttribute("aria-pressed",String(dark));b.textContent=dark?b.dataset.dark:b.dataset.light;}
  }
  update();prefers.addEventListener("change",update);
  document.addEventListener("DOMContentLoaded",()=>{
    update();const b=document.querySelector(".theme");
    b.onclick=()=>{saved=root.dataset.theme==="dark"?"light":"dark";try{localStorage.setItem("theoriacast.theme",saved);}catch{}update();};
    const other=document.querySelector("a[hreflang]");other.onclick=()=>{other.hash=location.hash;};
    if(location.hostname==="theoriacast.online"&&root.lang==="en")document.querySelector(".home").href="/en/";
  });
})();
