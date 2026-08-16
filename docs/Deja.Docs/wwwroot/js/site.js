// Small, dependency-free helpers the Blazor app calls via JS interop.
window.dejaDocs = (function () {
    let tocObserver = null;

    function slugify(text) {
        return text.toLowerCase().trim()
            .replace(/[^\w\s-]/g, '')
            .replace(/\s+/g, '-');
    }

    // <base href="/"> makes bare "#id" resolve to the home page, so qualify with the current path
    function fragmentHref(id) {
        return location.pathname + location.search + '#' + id;
    }

    function ensureHeadingIds(article) {
        const seen = new Set();
        article.querySelectorAll('h2, h3').forEach(h => {
            if (!h.id) {
                let id = slugify(h.textContent.replace(/#$/, ''));
                while (seen.has(id)) id += '-x';
                h.id = id;
            }
            seen.add(h.id);

            const existing = h.querySelector('.heading-anchor');
            if (existing) {
                existing.href = fragmentHref(h.id);
            } else {
                const a = document.createElement('a');
                a.className = 'heading-anchor';
                a.href = fragmentHref(h.id);
                a.textContent = '#';
                a.setAttribute('aria-label', 'Link to this section');
                h.appendChild(a);
            }
        });
    }

    function buildToc() {
        const article = document.querySelector('.docs-article');
        const toc = document.getElementById('toc-list');
        if (!article || !toc) return;

        ensureHeadingIds(article);

        const headings = Array.from(article.querySelectorAll('h2, h3'));
        toc.innerHTML = '';

        for (const h of headings) {
            const li = document.createElement('li');
            li.className = h.tagName === 'H3' ? 'toc-h3' : 'toc-h2';
            const a = document.createElement('a');
            a.href = fragmentHref(h.id);
            a.dataset.tocTarget = h.id;
            a.textContent = h.textContent.replace(/#$/, '');
            li.appendChild(a);
            toc.appendChild(li);
        }

        const tocContainer = toc.closest('.docs-toc');
        if (tocContainer) tocContainer.style.display = headings.length ? '' : 'none';

        if (tocObserver) tocObserver.disconnect();
        tocObserver = new IntersectionObserver(entries => {
            for (const entry of entries) {
                if (!entry.isIntersecting) continue;
                toc.querySelectorAll('a').forEach(a => a.classList.remove('active'));
                const active = toc.querySelector('a[data-toc-target="' + entry.target.id + '"]');
                if (active) active.classList.add('active');
            }
        }, { rootMargin: '-15% 0px -75% 0px' });

        headings.forEach(h => tocObserver.observe(h));
    }

    function highlight(element) {
        if (!window.Prism || !element) return;
        element.querySelectorAll('pre code[class*="language-"]').forEach(c => Prism.highlightElement(c));
    }

    function setTheme(theme) {
        if (theme === 'dark' || theme === 'light') {
            document.documentElement.setAttribute('data-theme', theme);
            localStorage.setItem('deja-theme', theme);
        } else {
            document.documentElement.removeAttribute('data-theme');
            localStorage.removeItem('deja-theme');
        }
    }

    function effectiveTheme() {
        const set = document.documentElement.getAttribute('data-theme');
        if (set) return set;
        return window.matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light';
    }

    // Copy buttons work through delegation so no per-block wiring is needed.
    document.addEventListener('click', async e => {
        const button = e.target.closest('.code-copy');
        if (!button) return;
        const code = button.closest('.code-block')?.querySelector('pre code');
        if (!code) return;
        try {
            await navigator.clipboard.writeText(code.textContent);
            const original = button.textContent;
            button.textContent = 'Copied!';
            setTimeout(() => { button.textContent = original; }, 1200);
        } catch {
            // Clipboard unavailable (e.g. insecure context); leave the button as-is.
        }
    });

    return { buildToc, highlight, setTheme, effectiveTheme };
})();
