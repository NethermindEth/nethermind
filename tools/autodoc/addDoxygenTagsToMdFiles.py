import os
import glob
import argparse

def find_source_file(source_dir, base_name):
    for root, _, files in os.walk(source_dir):
        for ext in ('*.c', '*.cpp', '*.h', '*.hpp', '*.java', '*.py', '*.cs', '*.vb', '*.fs', '*.aspx', '*.ascx', '*.asmx', '*.ashx', '*.axd', '*.master', '*.sitemap', '*.config', '*.csproj', '*.vbproj', '*.fsproj', '*.asax', '*.svc', '*.xaml', '*.props'):
            for file in files:
                if file == f"{base_name}{ext[1:]}":
                    return os.path.join(root, file)
    return None

def add_doxygen_tag(md_path, source_file):
    try:
        with open(md_path, 'r', encoding='utf-8') as md_file:
            lines = md_file.readlines()
    except UnicodeDecodeError as e:
        print(f"Error reading file {md_path}: {e}")
        return

    relative_path = os.path.relpath(source_file, os.path.dirname(md_path))
    relative_path = relative_path.replace('\\', '/')
    doxygen_tag = f"\\file {relative_path}\n"

    if lines and lines[0].startswith("\\file"):
        lines[0] = doxygen_tag
    else:
        lines.insert(0, doxygen_tag)

    try:
        with open(md_path, 'w', encoding='utf-8') as md_file:
            md_file.writelines(lines)
    except UnicodeEncodeError as e:
        print(f"Error writing to file {md_path}: {e}")


def process_md_files(source_dir, md_dir):
    for root, _, files in os.walk(md_dir):
        for file in files:
            if file.endswith('.md'):
                print(file)
                base_name = os.path.splitext(file)[0]
                md_path = os.path.join(root, file)
                source_file = find_source_file(source_dir, base_name)

                if source_file:
                    add_doxygen_tag(md_path, source_file)

def main():
    parser = argparse.ArgumentParser(description="Add Doxygen tags to markdown files relating them to source files.")
    parser.add_argument("--source_dir", required=True, help="Path to the source files directory")
    parser.add_argument("--md_dir", required=True, help="Path to the markdown files directory")
    args = parser.parse_args()

    process_md_files(args.source_dir, args.md_dir)

if __name__ == "__main__":
    main()
