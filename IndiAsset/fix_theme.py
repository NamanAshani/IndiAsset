import os
import re

def process_file(filepath):
    if not os.path.exists(filepath): return
    with open(filepath, 'r', encoding='utf-8') as f:
        content = f.read()
    
    # Remove emojis (common ranges)
    emoji_pattern = re.compile(r'[\U00010000-\U0010ffff]', flags=re.UNICODE)
    content = emoji_pattern.sub(r'', content)
    
    # Theme replacements
    replacements = {
        'text-dark': 'text-white',
        'bg-white': 'bg-dark',
        'bg-light': 'bg-secondary bg-opacity-25'
    }
    for old, new in replacements.items():
        content = content.replace(old, new)
        
    with open(filepath, 'w', encoding='utf-8') as f:
        f.write(content)
    print(f"Processed {filepath}")

process_file('Views/Booking/MyBookings.cshtml')
process_file('Views/Home/Index.cshtml')
