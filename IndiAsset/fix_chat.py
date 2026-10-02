import os
import re

filepath = 'Views/Chat/Index.cshtml'
if not os.path.exists(filepath):
    print("File not found")
else:
    with open(filepath, 'r', encoding='utf-8') as f:
        content = f.read()

    # Remove emojis (common ranges)
    emoji_pattern = re.compile(r'[\U00010000-\U0010ffff]', flags=re.UNICODE)
    content = emoji_pattern.sub(r'', content)

    # Manual color/theme replacements for the massive Chat view CSS & HTML
    # We will replace background colors to dark theme equivalents
    content = content.replace('background: #ffffff;', 'background: #1a1d27;')
    content = content.replace('background: #f8fafc;', 'background: #1e2130;')
    content = content.replace('border-right: 1px solid #edf2f7;', 'border-right: 1px solid rgba(255,255,255,0.06);')
    content = content.replace('border-bottom: 1px solid #edf2f7;', 'border-bottom: 1px solid rgba(255,255,255,0.06);')
    content = content.replace('border-top: 1px solid #edf2f7;', 'border-top: 1px solid rgba(255,255,255,0.06);')
    content = content.replace('border: 1px solid #e2e8f0;', 'border: 1px solid rgba(255,255,255,0.1);')
    
    # Text colors
    content = content.replace('color: #0b2b4a;', 'color: #f1f5f9;')
    content = content.replace('color: #1e293b;', 'color: #e2e8f0;')
    content = content.replace('color: #64748b;', 'color: #94a3b8;')
    
    # Message bubbles
    content = content.replace('.message-bubble-wrapper.received .message-bubble {\n            background: #ffffff;', '.message-bubble-wrapper.received .message-bubble {\n            background: #252836;')

    # Bootstrap utility classes
    content = content.replace('bg-white', 'bg-dark')
    content = content.replace('bg-light', 'bg-secondary bg-opacity-25')
    content = content.replace('text-dark', 'text-white')

    with open(filepath, 'w', encoding='utf-8') as f:
        f.write(content)
    print("Chat View updated for Dark Theme")
