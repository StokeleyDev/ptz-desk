import React from 'react';

// Plain line icons (no emoji, no font glyphs). Everything draws with currentColor.
const paths = {
  speaker: <><path d="M4 9.5v5h3.5L12 18.5v-13L7.5 9.5H4z" /><path d="M15.5 9a4 4 0 0 1 0 6M18 6.5a7.5 7.5 0 0 1 0 11" /></>,
  speakerMuted: <><path d="M4 9.5v5h3.5L12 18.5v-13L7.5 9.5H4z" /><path d="M16 9.5l5 5M21 9.5l-5 5" /></>,
  mic: <><rect x="9" y="3.5" width="6" height="11" rx="3" /><path d="M5.5 11.5a6.5 6.5 0 0 0 13 0M12 18v3" /></>,
  micMuted: <><rect x="9" y="3.5" width="6" height="11" rx="3" /><path d="M5.5 11.5a6.5 6.5 0 0 0 13 0M12 18v3M4 4l16 16" /></>,
  rooms: <><rect x="3.5" y="4" width="7" height="7" rx="1.5" /><rect x="13.5" y="4" width="7" height="7" rx="1.5" /><rect x="3.5" y="13" width="7" height="7" rx="1.5" /><rect x="13.5" y="13" width="7" height="7" rx="1.5" /></>,
  remote: <><rect x="7" y="3" width="10" height="18" rx="2" /><path d="M10.5 17.5h3" /></>,
  power: <><path d="M12 3.5v8" /><path d="M7.2 6.7a7.5 7.5 0 1 0 9.6 0" /></>,
  retry: <><path d="M19.5 12a7.5 7.5 0 1 1-2.2-5.3" /><path d="M19.5 4.5v4h-4" /></>,
  edit: <><path d="M4 20h4L19 9l-4-4L4 16v4z" /><path d="M13.5 6.5l4 4" /></>,
  gear: <><circle cx="12" cy="12" r="3" /><path d="M12 3v2.5M12 18.5V21M3 12h2.5M18.5 12H21M5.6 5.6l1.8 1.8M16.6 16.6l1.8 1.8M18.4 5.6l-1.8 1.8M7.4 16.6l-1.8 1.8" /></>,
  close: <path d="M6 6l12 12M18 6L6 18" />,
  chevron: <path d="M6.5 14.5L12 9l5.5 5.5" />,
  sun: <><circle cx="12" cy="12" r="4" /><path d="M12 2.5v2M12 19.5v2M2.5 12h2M19.5 12h2M5.3 5.3l1.4 1.4M17.3 17.3l1.4 1.4M18.7 5.3l-1.4 1.4M6.7 17.3l-1.4 1.4" /></>,
  moon: <path d="M19.5 14.5A7.5 7.5 0 0 1 9.5 4.5a7.5 7.5 0 1 0 10 10z" />,
  trash: <><path d="M5 7h14M10 4h4M7 7l1 13h8l1-13" /></>,
};

export function Icon({ name, size = 18, ...rest }) {
  return <svg className="icon" width={size} height={size} viewBox="0 0 24 24" fill="none" stroke="currentColor"
    strokeWidth="1.7" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true" focusable="false" {...rest}>{paths[name]}</svg>;
}
