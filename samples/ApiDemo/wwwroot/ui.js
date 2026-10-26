// ApiDemo 的极简 UI 工具：声明式的"一条 API 一行"。
//
// 为什么不用框架：这个应用要验证的是**库**，不是前端工程。一个 HTML + 几个 ES 模块，
// 没有构建步骤——`dotnet run` 直接看到的就是源码，不存在"示例里的代码和包里的不一样"。

let sink = () => {};

/** 由 app.js 接管日志渲染（ui 只负责产生条目）。 */
export function setLogSink(fn) {
    sink = fn;
}

/** 记一条日志；`level` 取 ok / err / warn / info。 */
export function log(text, level = "info") {
    sink({ text, level, time: new Date() });
}

/** 创建元素。`attrs` 里以 on 开头的函数会注册为事件处理器。 */
export function h(tag, attrs = {}, ...children) {
    const element = document.createElement(tag);

    for (const [key, value] of Object.entries(attrs ?? {})) {
        if (value === undefined || value === null || value === false) {
            continue;
        }

        if (key.startsWith("on") && typeof value === "function") {
            element.addEventListener(key.slice(2).toLowerCase(), value);
            continue;
        }

        if (key === "text") {
            element.textContent = String(value);
            continue;
        }

        if (key === "class") {
            element.className = value;
            continue;
        }

        if (key === "checked" || key === "disabled" || key === "hidden") {
            element[key] = Boolean(value);
            continue;
        }

        if (key === "style" && typeof value === "object") {
            Object.assign(element.style, value);
            continue;
        }

        element.setAttribute(key, value === true ? "" : String(value));
    }

    append(element, children);
    return element;
}

function append(parent, children) {
    for (const child of children) {
        if (child === undefined || child === null || child === false) {
            continue;
        }
        if (Array.isArray(child)) {
            append(parent, child);
        } else if (child instanceof Node) {
            parent.append(child);
        } else {
            parent.append(document.createTextNode(String(child)));
        }
    }
}

/** 把任意返回值变成一行可读文本。字符串不加引号——这里的结果多是路径与文案。 */
export function fmt(value) {
    if (value === undefined) {
        return "undefined";
    }
    if (value === null) {
        return "null";
    }
    if (value instanceof Error) {
        return errorText(value);
    }
    if (value instanceof Uint8Array) {
        const head = Array.from(value.slice(0, 12)).join(", ");
        return `Uint8Array(${value.length}) [${head}${value.length > 12 ? ", …" : ""}]`;
    }
    if (typeof value === "string") {
        return value.length > 700 ? `${value.slice(0, 700)}…` : value;
    }
    if (typeof value === "number" || typeof value === "boolean" || typeof value === "bigint") {
        return String(value);
    }

    try {
        const text = JSON.stringify(value, (_key, item) =>
            item instanceof Uint8Array ? `<${item.length} bytes>` : item);
        return text.length > 700 ? `${text.slice(0, 700)}…` : text;
    } catch {
        return String(value);
    }
}

/** 错误形状：message + code + data（后两者只在宿主给了时存在）。 */
export function errorText(error) {
    if (!(error instanceof Error)) {
        return fmt(error);
    }

    let text = error.message;
    if (error.code) {
        text += `  [code=${error.code}]`;
    }
    if (error.data !== undefined) {
        text += `\n  data=${fmt(error.data)}`;
    }
    return text;
}

/** 一组 API。返回容器，调用方往里塞 action / block。 */
export function section(parent, group) {
    const element = h("section", { class: "group" });
    const header = h("header", {}, h("h2", {}, group.title));

    if (group.note) {
        header.append(h("p", { class: "group-note" }, group.note));
    }

    const count = (group.items ?? []).filter((item) => typeof item.run === "function").length;
    if (count > 0) {
        header.append(h("span", { class: "group-count" }, `${count} 项`));
    }

    element.append(header);
    parent.append(element);
    return element;
}

/**
 * 一条 API：左侧名字与说明，右侧参数 + 运行按钮，下方回执。
 *
 * `spec.run(values)` 的返回值会显示在行内并写进日志；抛错则显示错误形状。
 * 参数一律以字符串收集，需要数字/布尔的由 `run` 自己转换——少一层隐式规则。
 */
export function action(parent, spec) {
    const row = h("div", { class: "action" });
    const info = h("div", { class: "action-info" });

    const name = h("div", { class: "action-name" }, h("code", {}, spec.name));
    if (spec.badge) {
        name.append(h("span", { class: "badge" }, spec.badge));
    }
    info.append(name);

    if (spec.note) {
        info.append(h("p", { class: "action-note" }, spec.note));
    }

    const controls = h("div", { class: "action-controls" });
    const inputs = {};

    for (const input of spec.inputs ?? []) {
        const field = h("input", {
            type: input.type ?? "text",
            value: input.value ?? "",
            placeholder: input.placeholder ?? "",
            title: input.title ?? "",
            checked: input.type === "checkbox" ? Boolean(input.checked) : undefined,
        });

        if (input.type === "checkbox") {
            controls.append(h("label", { class: "arg-label" }, field, input.label ?? ""));
        } else {
            if (input.width) {
                field.style.width = `${input.width}px`;
            }
            controls.append(h("label", { class: "arg-label" }, input.label ?? "", field));
        }

        inputs[input.key] = field;
    }

    const out = h("span", { class: "result" });
    const button = h("button", {
        class: spec.danger ? "run danger" : "run",
    }, spec.button ?? "运行");

    button.addEventListener("click", async () => {
        if (spec.confirm && !globalThis.confirm(spec.confirm)) {
            return;
        }

        const values = {};
        for (const [key, field] of Object.entries(inputs)) {
            values[key] = field.type === "checkbox" ? field.checked : field.value;
        }

        button.disabled = true;
        out.className = "result pending";
        out.textContent = "运行中…";

        const started = performance.now();
        try {
            const value = await spec.run(values);
            const ms = Math.round(performance.now() - started);
            const text = spec.format ? spec.format(value) : fmt(value);
            out.className = "result ok";
            out.textContent = text;
            log(`${spec.name} → ${text}  (${ms} ms)`, "ok");
        } catch (error) {
            const text = errorText(error);
            out.className = "result err";
            out.textContent = text;
            log(`${spec.name} ✗ ${text}`, "err");
        } finally {
            button.disabled = false;
        }
    });

    controls.append(button);
    row.append(info, controls, out);
    parent.append(row);
    return row;
}

/** 自定义块（进度条、拖放区这类不方便压成一行按钮的东西）。 */
export function block(parent, title) {
    const element = h("div", { class: "block" });
    if (title) {
        element.append(h("h3", {}, title));
    }
    parent.append(element);
    return element;
}

/** 键值表，用于只读事实（平台、路径、枚举）。 */
export function kv(parent, entries) {
    const list = h("dl", { class: "kv" });
    for (const [key, value] of Object.entries(entries)) {
        list.append(h("dt", {}, key), h("dd", {}, fmt(value)));
    }
    parent.append(list);
    return list;
}
