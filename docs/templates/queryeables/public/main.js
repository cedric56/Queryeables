export default {
  iconLinks: [
    { icon: 'github', href: 'https://github.com/cedric56/Queryeables', title: 'GitHub' }
  ],
  start: () => {
    // The hero diagram animates with SMIL: stop it for readers who asked for less motion.
    if (window.matchMedia('(prefers-reduced-motion: reduce)').matches)
      document.querySelectorAll('.qy-flow svg').forEach(svg => svg.pauseAnimations && svg.pauseAnimations());
  }
}
