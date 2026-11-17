import AppKit
import SwiftUI

// History list and rows, with the status and outcome text they show.

extension SettingsView {
    private var visibleHistory: [HistoryEntry] {
        controller.history.filter { entry in
            searchText.isEmpty || entry.finalText.localizedCaseInsensitiveContains(searchText) || entry.rawText.localizedCaseInsensitiveContains(searchText)
        }
    }

    /// Consecutive entries from the same day, in history order (newest first).
    private var historyGroups: [(day: String, entries: [HistoryEntry])] {
        var groups: [(day: String, entries: [HistoryEntry])] = []
        for entry in visibleHistory {
            let calendar = Calendar.current, date = entry.recordedAt
            let day = calendar.isDateInToday(date) ? "Today"
                : calendar.isDateInYesterday(date) ? "Yesterday"
                : calendar.isDate(date, equalTo: Date(), toGranularity: .year) ? date.formatted(.dateTime.month(.abbreviated).day())
                : date.formatted(.dateTime.month(.abbreviated).day().year())
            if let last = groups.last?.entries.first, calendar.isDate(last.recordedAt, inSameDayAs: date) { groups[groups.count - 1].entries.append(entry) } else { groups.append((day, [entry])) }
        }
        return groups
    }

    var history: some View {
        VStack(spacing: 0) {
            VStack(alignment: .leading, spacing: 12) {
                Text("History").font(WindowType.display(64)).tracking(-1.28)
                help(controller.historySummary)
                HStack(spacing: 8) {
                    field("Search transcripts", text: $searchText)
                    if !searchText.isEmpty {
                        Button { searchText = "" } label: { Image(systemName: "xmark") }
                            .buttonStyle(.plain).foregroundStyle(Palette.ink70).accessibilityLabel("Clear search")
                    }
                }
            }
            .padding(EdgeInsets(top: 16, leading: 24, bottom: 16, trailing: 24))
            .overlay(alignment: .bottom) { rule }

            ScrollViewReader { scroller in
            ScrollView {
                let groups = historyGroups
                LazyVStack(spacing: 0) {
                    if groups.isEmpty {
                        VStack(spacing: 8) {
                            Text(controller.history.isEmpty ? "Your dictation appears here" : "No matching transcripts").font(WindowType.body(26, weight: .semibold))
                            help(controller.history.isEmpty ? "Completed transcripts are saved here before they are inserted." : "Try a different search.")
                            if controller.history.isEmpty && !setupComplete {
                                Button("Set Up Murmur") { screen = .setup }.buttonStyle(InkButtonStyle()).padding(.top, 8)
                            }
                        }
                        .padding(.vertical, 80).frame(maxWidth: .infinity)
                    }
                    ForEach(groups.indices, id: \.self) { index in
                        let group = groups[index]
                        HStack(alignment: .top, spacing: 16) {
                            VStack(alignment: .leading, spacing: 4) {
                                mono(group.day)
                                mono(group.entries.count == 1 ? "1 entry" : "\(group.entries.count) entries", color: Palette.ink64)
                            }
                            .frame(width: 80, alignment: .leading).padding(.top, 14)
                            VStack(spacing: 0) {
                                ForEach(group.entries) { entry in historyRow(entry) }
                            }
                        }
                        .padding(.vertical, 24).overlay(alignment: .bottom) { rule }
                    }
                }
                .padding(.horizontal, 24)
            }
            .onAppear { scrollToTarget(scroller) }
            .onChange(of: scrollTarget) { scrollToTarget(scroller) }
            }

            HStack {
                help(controller.historyMode == .on ? "Saved on this Mac only. \(retentionText)" : controller.historySummary)
                Spacer()
                Button("Clear History…", role: .destructive, action: controller.clearHistory).buttonStyle(InkButtonStyle(kind: .outline))
                    .disabled(controller.history.isEmpty)
            }
            .padding(.horizontal, 24).frame(height: 56).overlay(alignment: .top) { rule }
        }
    }

    private func scrollToTarget(_ scroller: ScrollViewProxy) {
        guard let id = scrollTarget else { return }
        scrollTarget = nil
        DispatchQueue.main.async { scroller.scrollTo(id, anchor: .top) }
    }

    private var retentionText: String {
        switch controller.retention {
        case .sixMonths: return "Kept for six months."
        case .forever: return "Kept forever."
        case .thirtyDays: return "Kept for 30 days."
        }
    }

    private func historyRow(_ entry: HistoryEntry) -> some View {
        let open = selectedID == entry.id, attention = entry.needsAttention
        let note = controller.sessionOnlyIDs.contains(entry.id) ? HistoryText.sessionOnlyNote : entry.statusNote
        return VStack(alignment: .leading, spacing: 0) {
            Button { selectedID = open ? nil : entry.id } label: {
                HStack(alignment: .top, spacing: 16) {
                    Image(systemName: open ? "chevron.down" : "chevron.right")
                        .font(.system(size: 11, weight: .semibold)).foregroundStyle(Palette.ink70)
                        .frame(width: 12).padding(.top, 5).accessibilityHidden(true)
                    Text(entry.recordedAt, format: .dateTime.hour().minute())
                        .font(.system(size: 13, design: .monospaced)).foregroundStyle(Palette.ink70).lineLimit(1).fixedSize().frame(width: 72, alignment: .leading).padding(.top, 2)
                    Text((entry.finalText.isEmpty ? entry.rawText : entry.finalText).replacingOccurrences(of: "\n", with: " "))
                        .font(WindowType.body(16)).lineSpacing(2).lineLimit(open ? 1 : 2).frame(maxWidth: .infinity, alignment: .leading)
                    if let label = entry.statusLabel {
                        mono(label, size: 11, color: attention ? Palette.paper : Palette.accentInk)
                            .lineLimit(1).fixedSize().padding(.horizontal, 8).padding(.vertical, 3)
                            .background(attention ? Palette.ink : Palette.accent16)
                    }
                }
                .padding(.vertical, 12).frame(minHeight: 56).contentShape(Rectangle())
            }
            .buttonStyle(.plain).accessibilityAddTraits(open ? .isSelected : [])
            .accessibilityValue(open ? "Expanded" : "Collapsed")
            .accessibilityHint("Show or hide transcript and recovery actions")
            if open {
                VStack(alignment: .leading, spacing: 0) {
                    if let note {
                        Text(note).font(WindowType.body(14)).foregroundStyle(Palette.ink80).fixedSize(horizontal: false, vertical: true)
                            .padding(.horizontal, 14).padding(.vertical, 12).frame(maxWidth: .infinity, alignment: .leading)
                            .background(Palette.accent16).padding(.bottom, 16)
                    }
                    eyebrow("Final text")
                    if editingTextID == entry.id {
                        TextEditor(text: $textDraft).font(WindowType.body(16)).scrollContentBackground(.hidden)
                            .frame(minHeight: 88).padding(8).overlay(RoundedRectangle(cornerRadius: 4).strokeBorder(Palette.ink20, lineWidth: 1))
                            .accessibilityLabel("Final text").padding(.top, 6)
                        HStack(spacing: 8) {
                            Button("Save") { if controller.editEntryText(entry, to: textDraft) { editingTextID = nil } }
                                .buttonStyle(InkButtonStyle(kind: .filled)).fixedSize()
                                .disabled(textDraft.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
                            Button("Cancel") { editingTextID = nil }.buttonStyle(InkButtonStyle(kind: .outline)).fixedSize()
                                .keyboardShortcut(.cancelAction)
                        }.padding(.top, 8)
                        Text("Murmur learns misheard words from your fix, as it does from edits after insertion.")
                            .font(WindowType.body(13)).foregroundStyle(Palette.ink64).padding(.top, 8)
                    } else {
                        Text(entry.finalText.isEmpty ? "No final text saved." : entry.finalText).font(WindowType.body(16)).lineSpacing(3)
                            .textSelection(.enabled).frame(maxWidth: .infinity, alignment: .leading).padding(.top, 6)
                    }
                    if let line = entry.outcomeNote {
                        Text(line).font(WindowType.body(13)).foregroundStyle(Palette.ink64).padding(.top, 6)
                    }
                    if let route = AppController.routeLabel(transcription: entry.transcriptionProvider, cleanup: entry.cleanupProvider, outcome: entry.cleanupOutcome) {
                        Text(route).font(WindowType.body(13)).foregroundStyle(Palette.ink64).padding(.top, 2)
                    }
                    if entry.rawText != entry.finalText {
                        eyebrow("What changed").padding(.top, 16)
                        WordDiffText(raw: entry.rawText, final: entry.finalText)
                            .font(WindowType.body(15)).lineSpacing(3)
                            .textSelection(.enabled).frame(maxWidth: .infinity, alignment: .leading).padding(.top, 6)
                        eyebrow("Raw transcription").padding(.top, 16)
                        Text(entry.rawText).font(WindowType.body(15)).lineSpacing(3).foregroundStyle(Palette.ink70)
                            .textSelection(.enabled).frame(maxWidth: .infinity, alignment: .leading).padding(.top, 6)
                    }
                    let actions = Group {
                        if !entry.finalText.isEmpty {
                            Button(copiedID == entry.id + "f" ? "Copied ✓" : "Copy Text") { controller.copyEntry(entry); flashCopied(entry.id + "f") }
                                .buttonStyle(InkButtonStyle())
                        }
                        if entry.rawText != entry.finalText {
                            Button(copiedID == entry.id + "r" ? "Copied ✓" : "Copy Raw") { controller.copyRawEntry(entry); flashCopied(entry.id + "r") }
                                .buttonStyle(InkButtonStyle(kind: .outline))
                        }
                        if !entry.finalText.isEmpty && editingTextID != entry.id {
                            Button("Edit") { textDraft = entry.finalText; editingTextID = entry.id }.buttonStyle(InkButtonStyle(kind: .outline))
                                .accessibilityLabel("Edit final text")
                        }
                        Button("Delete", role: .destructive) { controller.deleteEntry(entry) }.buttonStyle(InkButtonStyle(kind: .plain))
                    }
                    ViewThatFits(in: .horizontal) {
                        HStack(spacing: 8) { actions }.fixedSize(horizontal: true, vertical: false)
                        VStack(alignment: .leading, spacing: 8) { actions }
                    }
                    .padding(.top, 18)
                    Text("Copying never retries insertion or sends a message.").font(WindowType.body(13)).foregroundStyle(Palette.ink64).padding(.top, 10)
                }
                .padding(.top, 4).padding(.bottom, 20)
            }
        }
        .overlay(alignment: .bottom) { Rectangle().fill(Palette.ink45).frame(height: 1) }
        .id(entry.id)
    }

    private func flashCopied(_ id: String) {
        copiedID = id
        DispatchQueue.main.asyncAfter(deadline: .now() + 1.5) { if copiedID == id { copiedID = nil } }
    }
}

/// The raw transcript against the final text: removed words struck through, added words in the accent color.
struct WordDiffText: View {
    let raw: String, final: String
    var body: some View {
        Text(WordDiff.runs(raw: raw, final: final).reduce(AttributedString()) { result, run in
            var piece = AttributedString(run.text + " ")
            switch run.kind {
            case .same: piece.foregroundColor = Palette.ink70
            case .removed: piece.strikethroughStyle = .single; piece.foregroundColor = Palette.ink45
            case .added: piece.foregroundColor = Palette.accent; piece.underlineStyle = .single
            }
            return result + piece
        })
        .accessibilityLabel("Changed from the raw transcript: \(WordDiff.changeCount(raw: raw, final: final)) words")
    }
}
